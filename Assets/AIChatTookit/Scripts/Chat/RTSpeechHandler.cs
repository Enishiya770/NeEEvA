using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;
/// <summary>
/// 麦克风实时聊天
/// </summary>
public class RTSpeechHandler : MonoBehaviour
{
    /// <summary>
    /// 麦克风名称
    /// </summary>
    public string m_MicrophoneName = null;
    /// <summary>
    /// 音量大于这个值，就开始录制
    /// </summary>
    public float m_SilenceThreshold = 0.01f;
    /// <summary>
    /// 沉默限制时长。中文/日文说话时句间停顿、思考停顿很容易超过2秒，
    /// 设短了会经常切到一半。设到3.5秒能容忍绝大多数自然停顿；
    /// 真正没说完的边界 case 交给 LLM 用 backchannel 处理 (见 behavior.txt)。
    /// </summary>
    [Header("设置几秒没声音，就停止录制")]
    public float m_RecordingTimeLimit = 3.5f;

    /// <summary>
    /// 录音"预卷"：每次StartRecording时，把起始位置往回拨这么多秒，
    /// 这样VAD触发那一刻之前(尤其是触发那个音节本身的前半部分)的音频也会被一起送给ASR。
    /// 没有这个，"换个话题吧"就经常被识别成"个话题吧"——开头的"换"字音节
    /// 因为VAD的RMS低通需要约100ms才把包络抬到阈值之上、当时已经被错过。
    /// 0.25s ≈ 4000 samples @ 16kHz，覆盖VAD滞后(~100ms)+一帧裕量足够。
    /// </summary>
    [Header("录音预卷时长(秒) — 防丢首音节")]
    public float m_RecordingPreRollSeconds = 0.25f;

    [Header("Tentative-EOU — 把'等满3.5s沉默'压成'短沉默+ASR尾部判定'")]
    /// <summary>
    /// 启用临时EOU：沉默达到m_TentativeEouSilence时先做一次预测识别，
    /// 看转写文本是否以终结性标点/语气词收尾——是就立即EOU，跳过m_RecordingTimeLimit的硬等待。
    /// </summary>
    public bool m_EnableTentativeEou = true;
    /// <summary>
    /// 临时EOU触发的沉默时长(秒)。比m_RecordingTimeLimit短得多，
    /// 一般0.5-0.8s——人类自然句间停顿在此区间，结合ASR尾部判定可避免误切。
    /// </summary>
    [Tooltip("沉默达到这个时长就先发一次预测ASR，看尾部是否说完")]
    public float m_TentativeEouSilence = 0.6f;
    /// <summary>
    /// 兜底：临时EOU失败/不确定时，仍走m_RecordingTimeLimit；这个开关也能把整个机制关掉。
    /// </summary>
    public bool m_LogTentativeEou = true;

    /// <summary>
    /// 对话状态保持时长。
    /// 设计哲学：陪伴语境下"沉默本身是有意义的"，10秒不说话就被踢回唤醒态太粗暴。
    /// 现在默认很大(=不靠这个超时退出)，由用户主动喊唤醒词或显式退出来切状态。
    /// 沉默处理交给 ChatSample 的 Agent Loop——LLM 自主感知时间流动并决定是否开口。
    /// </summary>
    [Header("设置对话状态保持时间(默认很大=不超时)")]
    public float m_LossAwakeTimeLimit = 99999f;
    /// <summary>
    /// barge-in：角色出声时，用户连续说话多久就算"打断"
    /// </summary>
    [Header("barge-in触发阈值(秒)")]
    public float m_BargeInTriggerSeconds = 0.3f;
    /// <summary>
    /// 锁定状态下，不记录静默时间
    /// </summary>
    [SerializeField]private bool m_LockState = false;
    /// <summary>
    /// 音频
    /// </summary>
    private AudioClip m_RecordedClip;
    /// <summary>
    /// 唤醒关键词
    /// </summary>
    [SerializeField]private string m_AwakeKeyWord=string.Empty;
    /// <summary>
    /// 唤醒状态
    /// </summary>
    [Header("标识当前是否处于唤醒状态")]
    [SerializeField]private bool m_AwakeState = false;
    /// <summary>
    /// 监听状态
    /// </summary>
    [SerializeField] private bool m_ListeningState = false;
    /// <summary>
    /// 录制状态
    /// </summary>
    [SerializeField] private bool m_IsRecording = false;
    /// <summary>
    /// 沉默计时器
    /// </summary>
    [SerializeField]private float m_SilenceTimer = 0.0f;
    /// <summary>
    /// barge-in持续说话计时器：角色出声时只看这个，跟m_SilenceTimer不冲突
    /// </summary>
    [SerializeField]private float m_BargeInTimer = 0.0f;
    /// <summary>
    /// 平滑后的RMS。语音的瞬时RMS会因音节微停顿/塞音爆发反复跨越阈值，
    /// 用一阶低通把这些毛刺抹掉再判别，barge-in才不会被微停顿"重置"。
    /// </summary>
    [SerializeField]private float m_SmoothedRms = 0.0f;
    /// <summary>
    /// 平滑系数：每帧 newSmoothed = (1-α)*old + α*current。
    /// 0.15 ≈ 时间常数 6 帧 ≈ 100ms，能盖住正常音节间隙又能跟上真实开始/结束。
    /// </summary>
    [Header("RMS低通系数(0~1，越小越稳)")]
    public float m_RmsSmoothAlpha = 0.15f;
    /// <summary>
    /// 沉默时计时器衰减速度(秒/秒)。1.0 = 沉默1s清零；2.0 = 0.5s清零。
    /// 用衰减代替硬重置，微停顿不会瞬间杀掉累计。
    /// </summary>
    [Header("沉默时barge-in计时器衰减速度")]
    public float m_BargeInDecayRate = 2.0f;

    /// <summary>
    /// 启用后会在Console打 EOU/barge-in/RMS 监视等评测日志。
    /// 跟ChatSample的m_LogStreamTimings对齐使用——两边都开才看得到完整链路。
    /// </summary>
    [Header("Debug：评测时打开看 EOU/barge-in/RMS 日志")]
    public bool m_LogTimings = true;
    /// <summary>
    /// AI出声期间，每多少帧打一次smoothed RMS——用来排查"barge-in触发不了"是不是麦克风灵敏度太低。
    /// 默认30帧≈0.5s，足够看到包络变化又不刷屏。
    /// </summary>
    public int m_RmsLogEveryNFrames = 30;
    /// <summary>
    /// barge-in计时器从0刚刚抬起来的时间戳。Interrupt()触发时打"用户连续说话Xs"。
    /// </summary>
    private float m_BargeInWindowStartTime = 0f;

    [Header("Agent感知 — 环境扰动通知 (Agent Loop 用)")]
    [Tooltip("非语音 spike 的 RMS 阈值。低于 m_SilenceThreshold(语音阈值)、用来识别咳嗽/翻身/叹息/键盘声等。" +
        "高于这个值就 ping 一下 ChatSample.OnEnvironmentSpike，让 agent loop 决定要不要把下次 tick 拉前。")]
    public float m_RmsSpikeThreshold = 0.005f;
    [Tooltip("两次环境 spike 通知之间的最小间隔(秒)，防止持续噪音刷屏")]
    public float m_EnvSpikeMinGapSec = 2f;

    // —— Agent感知运行时状态 ——
    /// <summary>上次给 ChatSample 发 spike 通知的时刻——做 m_EnvSpikeMinGapSec 间隔的去抖</summary>
    private float m_LastEnvSpikeNotifyTime = -1f;

    /// <summary>
    /// 当前用户发言在ring buffer里的起始样本位置(含pre-roll)。
    /// -1 = 当前不在录用户发言。loop=true buffer是个环，所以这个值可能比当前GetPosition大(刚wrap过)。
    /// SnapshotFromBuffer会按环形语义处理跨边界的拷贝。
    /// </summary>
    private int m_RecordingStartPos = -1;

    // —— Tentative-EOU 运行时状态 ——
    /// <summary>当前正在做预测ASR(送了clip在等回包)。第二次沉默到点不重发——避免刷请求</summary>
    private bool m_TentativePreviewInFlight = false;
    /// <summary>临时EOU机制的轮次序号。每次发预测ASR递增，回调用它判断是否过期</summary>
    private int m_TentativeSeq = 0;
    /// <summary>临时EOU已经派发但还没确认。3.5s硬规则触发或用户重新开口时清掉</summary>
    private bool m_TentativeFired = false;
    /// <summary>预测ASR派发时的时间戳——用来日志显示"省了多少秒"</summary>
    private float m_TentativePreviewSentTime = 0f;

    /// <summary>
    /// 聊天脚本。指向场景里那个跑流式ASR/LLM/TTS管线的ChatSample——
    /// 它对外暴露了IsAISpeaking/Interrupt/AcceptClip/OnAISpeakDone这套实时对话需要的接口。
    /// </summary>
    [SerializeField]private ChatSample m_ChatSample;
    /// <summary>
    /// 语音唤醒。可选——留空则跳过整套唤醒词监听逻辑，
    /// 改由"实时对话"toggle按钮显式控制对话开关。
    /// </summary>
    [Header("可选：唤醒词模块。留空则只用toggle按钮控制")]
    [SerializeField] private WOV m_VoiceAWake;

    /// <summary>
    /// 实时对话开关按钮。点一下进入持续监听+barge-in的对话模式，再点一下退出。
    /// 这条路径绕开唤醒词仪式，给桌面/VR单人场景更直接的体验。
    /// </summary>
    [Header("可选：实时对话toggle按钮")]
    [SerializeField] private Button m_RealtimeToggleBtn;
    [SerializeField] private Text m_RealtimeBtnLabel;
    [SerializeField] private string m_LabelOff = "点击启用实时对话";
    [SerializeField] private string m_LabelOn = "实时对话中（点击关闭）";
    /// <summary>
    /// 启用时是否播放问候语(如果m_GreatingVoice配置了的话)，
    /// 模拟原唤醒词路径的"对方应了一声"感觉。
    /// </summary>
    [SerializeField] private bool m_PlayGreetingOnEnable = true;

    private void Awake()
    {
        OnInit();
    }

    private void OnInit()
    {
        //AI回复结束回调
        if (m_ChatSample != null)
        {
            m_ChatSample.OnAISpeakDone += SpeachDoneCallBack;
        }

        //唤醒词模块可选——配置了才绑定
        if (m_VoiceAWake != null)
        {
            m_VoiceAWake.OnBindAwakeCallBack(AwakeCallBack);
        }

        //实时对话toggle按钮可选——配置了才绑定
        if (m_RealtimeToggleBtn != null)
        {
            m_RealtimeToggleBtn.onClick.AddListener(ToggleRealtimeMode);
            UpdateRealtimeBtnLabel();
        }
    }

    private void Start()
    {

        if (m_MicrophoneName == null)
        {
            // 如果没有指定麦克风名称，则使用系统默认麦克风
            m_MicrophoneName = Microphone.devices[0];
        }

        // 确保麦克风准备好
        if (Microphone.IsRecording(m_MicrophoneName))
        {
            Microphone.End(m_MicrophoneName);
        }

        // 启动麦克风监听。loop=true 是关键：实时对话路径下 mic 一直跑，
        // 只有用户真的在说话才会被StopRecording短暂End掉再立刻重启。
        // 用loop=false的话，启用后只要30秒没有人说话(没触发StopRecording重启mic)，
        // buffer就被填满、mic自动停录，GetData会报"invalid parameter"——
        // Agent Loop 长时间没用户开口的场景必触发这个bug。
        m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);

        while (Microphone.GetPosition(null) <= 0) { }

        // 启动录制状态检测协程
        StartCoroutine(DetectRecording());
    }

    /// <summary>
    /// 开始检测声音
    /// </summary>
    /// <returns></returns>
    private IEnumerator DetectRecording()
    {
        while (true)
        {
            //守卫：万一mic被外部停掉(loop=false过期、设备断开、其他脚本调End)，
            //GetPosition会一直返回0/无效值，整个VAD永远不工作。检测到就重启mic。
            //loop=true保证沉默期间buffer不会被填满自动停录(那个bug已经在Start里修了，这里做兜底)。
            if (!Microphone.IsRecording(m_MicrophoneName))
            {
                if (m_LogTimings) Debug.LogWarning("[RTSpeech] mic未在录制，重启监听buffer");
                m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);
                yield return null;
                continue;
            }

            float[] samples = new float[128]; // 选择合适的样本大小
            int position = Microphone.GetPosition(null);
            if (position < samples.Length)
            {
                yield return null;
                continue;
            }

            //GetData在clip无效/位置越界时会抛 + Unity native log一条红色错误。
            //失败就跳过这帧，下一帧重试——这一帧的rms保持上次的值不会误触发。
            bool dataOk = false;
            try
            {
                m_RecordedClip.GetData(samples, position - samples.Length);
                dataOk = true;
            }
            catch (System.Exception e)
            {
                if (m_LogTimings) Debug.LogWarning($"[RTSpeech] GetData失败: {e.Message}");
            }
            if (!dataOk)
            {
                yield return null;
                continue;
            }

            float rms = 0.0f;
            foreach (float sample in samples)
            {
                rms += sample * sample;
            }

            rms = Mathf.Sqrt(rms / samples.Length);

            //一阶低通：把音节间的微停顿/塞音爆发抹平，留下真正的"说话包络"。
            //裸rms会在每个音节边界都跌破阈值，导致计时器频繁清零、永远累不到0.3s。
            m_SmoothedRms = (1f - m_RmsSmoothAlpha) * m_SmoothedRms + m_RmsSmoothAlpha * rms;

            //barge-in分支：角色正在出声时，VAD的语义不是"开新一轮录音"而是"用户在打断"
            //单独处理可以避免和正常录音逻辑互相打架(m_LockState、m_IsRecording等)
            bool aiSpeaking = (m_ChatSample != null && m_ChatSample.IsAISpeaking);
            if (aiSpeaking)
            {
                //AI开始说话意味着上一轮已被某条路径(StopRecording或ConfirmEouFromPreview)送进LLM——
                //还在飞的tentative预测ASR都不再相关，让其seq过期不再回写
                if (m_TentativeFired || m_TentativePreviewInFlight)
                {
                    InvalidateTentativeEou("AI-started-speaking");
                }
                //周期性吐smoothed RMS，方便确认麦克风灵敏度——
                //如果AI说话期间你大声说话但smoothed_rms始终在0.005以下，说明麦输入太弱或阈值太高
                if (m_LogTimings && m_RmsLogEveryNFrames > 0
                    && Time.frameCount % m_RmsLogEveryNFrames == 0)
                {
                    Debug.Log($"[RMS] smoothed={m_SmoothedRms:F4} threshold={m_SilenceThreshold:F4} timer={m_BargeInTimer:F2}s");
                }

                if (m_SmoothedRms > m_SilenceThreshold)
                {
                    //timer从0刚抬起来的瞬间记录"用户开口"时刻，用于Interrupt时算累积说话时长
                    if (m_BargeInTimer <= 0f)
                    {
                        m_BargeInWindowStartTime = Time.realtimeSinceStartup;
                    }
                    m_BargeInTimer += Time.deltaTime;
                    if (m_BargeInTimer >= m_BargeInTriggerSeconds)
                    {
                        if (m_LogTimings)
                        {
                            float spoken = Time.realtimeSinceStartup - m_BargeInWindowStartTime;
                            Debug.Log($"[Timing] ★ Barge-in 触发：用户连续说话 {spoken:F2}s (阈值 {m_BargeInTriggerSeconds:F2}s)");
                        }
                        PrintLog("Barge-in->用户打断角色发言");
                        m_ChatSample.Interrupt();
                        m_BargeInTimer = 0f;
                        m_BargeInWindowStartTime = 0f;
                        //打断后立刻进入正常录制：mic应该已经在跑(StopRecording改造后不再End)
                        //OnAISpeakDone也会触发一次ReStartRecord做兜底
                        if (m_AwakeState && !m_IsRecording)
                        {
                            StartRecording();
                        }
                    }
                }
                else
                {
                    //不硬清零——微停顿/弱辅音段也让timer慢慢衰减就行，
                    //真正长时间安静(比如用户其实没在说)才会自然归零。
                    m_BargeInTimer = Mathf.Max(0f, m_BargeInTimer - m_BargeInDecayRate * Time.deltaTime);
                    if (m_BargeInTimer <= 0f) m_BargeInWindowStartTime = 0f;
                }
                yield return null;
                continue;
            }
            else
            {
                m_BargeInTimer = 0f;
                m_BargeInWindowStartTime = 0f;
            }

            if (rms > m_SilenceThreshold)
            {
                m_SilenceTimer = 0.0f; // 重置静默计时器

                //用户重新开口——使任何在飞的预测ASR失效。回包时seq不匹配会被丢弃。
                //不直接拉m_TentativeFired=false——让in-flight回包按seq判老化即可
                if (m_TentativeFired || m_TentativePreviewInFlight)
                {
                    InvalidateTentativeEou("user-resumed");
                }

                //启动关键词唤醒监听(仅在配置了唤醒词模块时)
                if (m_VoiceAWake != null && !m_AwakeState && !m_ListeningState)
                {
                    StartVoiceListening();
                }
                //已唤醒，启动录制
                if (m_AwakeState&&!m_IsRecording)
                {
                    StartRecording();
                }

            }
            else
            {

                if (!m_LockState)
                {
                    m_SilenceTimer += Time.deltaTime;
                }

                //结束唤醒词监听(仅在配置了唤醒词模块时)
                if (m_VoiceAWake != null && m_ListeningState && !m_AwakeState && m_SilenceTimer >= m_RecordingTimeLimit)
                {
                    StopVoiceListening();
                }

                //—— Tentative-EOU：短沉默触发预测ASR，看尾部说没说完 ——
                //条件：开关开 + 在录用户语音 + 沉默达到短阈值 + 还没派发过 + 没有正在飞的预测
                if (m_EnableTentativeEou
                    && m_AwakeState && m_IsRecording
                    && m_SilenceTimer >= m_TentativeEouSilence
                    && !m_TentativeFired
                    && !m_TentativePreviewInFlight)
                {
                    TryFireTentativeEou();
                }

                //唤醒状态，结束说话(硬兜底：m_RecordingTimeLimit无声音强制EOU)
                if (m_AwakeState && m_IsRecording && m_SilenceTimer >= m_RecordingTimeLimit)
                {
                    //硬兜底到了——任何还在飞的预测ASR都已经过期，废掉它的seq
                    if (m_TentativeFired || m_TentativePreviewInFlight)
                    {
                        InvalidateTentativeEou("hard-timeout");
                    }
                    StopRecording();
                }

                //沉默时间过长，结束对话状态，进入等待唤醒
                //(默认m_LossAwakeTimeLimit=99999即此分支永不触发，由用户主动控制状态)
                if (m_AwakeState && !m_IsRecording && m_SilenceTimer >= m_LossAwakeTimeLimit)
                {
                    m_AwakeState=false;
                    PrintLog("Loss->对话连接已丢失");
                }

                //—— Agent 感知：环境扰动通知 ——
                //空闲(用户没在录、AI没在说)状态下，rms 抬到 spike 阈值就 ping 一下 ChatSample，
                //让 agent loop 决定要不要把下次 tick 拉到现在(模拟"被外界声音拽回注意力")。
                //不再做 Silence 阈值判定/累计计数/× K 之类——那些策略全交给 LLM。
                bool isIdle = m_AwakeState && !m_IsRecording;
                if (isIdle && rms > m_RmsSpikeThreshold && m_ChatSample != null)
                {
                    float now = Time.realtimeSinceStartup;
                    if (m_LastEnvSpikeNotifyTime < 0
                        || now - m_LastEnvSpikeNotifyTime >= m_EnvSpikeMinGapSec)
                    {
                        m_LastEnvSpikeNotifyTime = now;
                        m_ChatSample.OnEnvironmentSpike(rms);
                    }
                }

            }

            yield return null;

        }
    }
    
    [SerializeField]private AudioSource m_Greeting;
    [SerializeField] private AudioClip m_GreatingVoice;
    /// <summary>
    /// 关键词监听回调（仅在配置了m_VoiceAWake时有效）
    /// </summary>
    /// <param name="_msg"></param>
    private void AwakeCallBack(string _msg)
    {
        if (_msg == m_AwakeKeyWord&&!m_AwakeState)
        {
            EnableRealtimeMode();
            Debug.Log("识别到关键词：" + _msg);
        }
    }

    /// <summary>
    /// 切换实时对话模式。绑定到toggle按钮。
    /// </summary>
    public void ToggleRealtimeMode()
    {
        if (m_AwakeState) DisableRealtimeMode();
        else EnableRealtimeMode();
    }

    /// <summary>
    /// 显式进入实时对话状态：开始持续VAD监听、可选播放问候语。
    /// 唤醒词路径和toggle按钮路径都收敛到这里，保证状态机入口唯一。
    /// </summary>
    public void EnableRealtimeMode()
    {
        if (m_AwakeState) return;
        m_AwakeState = true;
        m_SilenceTimer = 0f;
        m_BargeInTimer = 0f;

        m_LastEnvSpikeNotifyTime = -1f;
        InvalidateTentativeEou("EnableRealtimeMode");
        m_RecordingStartPos = -1;  //上一次会话残留的起点位置作废

        //启动 Agent Loop —— 让角色拥有时间感、自主决定说话节奏
        if (m_ChatSample != null) m_ChatSample.StartAgentLoop();

        //保证mic在跑——刚启动应用时已经在跑了，但用户可能先关闭再开启，这里兜底
        if (!Microphone.IsRecording(m_MicrophoneName))
        {
            m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);
        }

        PrintLog("Link->实时对话已启用");
        UpdateRealtimeBtnLabel();

        //如果用户保留了问候语配置，启用时也播一下，不然进入对话太"无声"
        if (m_PlayGreetingOnEnable && m_Greeting != null && m_GreatingVoice != null)
        {
            m_Greeting.clip = m_GreatingVoice;
            m_Greeting.Play();
        }
    }

    /// <summary>
    /// 显式退出实时对话状态。如果当前正在录用户发言就丢弃录音；
    /// 如果AI正在说话则不强行打断（让她自然说完，用户主动关闭应该尊重她的话）。
    /// </summary>
    public void DisableRealtimeMode()
    {
        if (!m_AwakeState) return;
        m_AwakeState = false;

        //正在录用户发言的话，丢弃 —— 用户既然关了实时对话，这段就别送ASR/LLM了
        //不再End/Start mic：保持continuous运行，只清掉m_RecordingStartPos表示"已经不在录"
        if (m_IsRecording)
        {
            m_IsRecording = false;
            m_RecordingStartPos = -1;
        }

        m_LockState = false;
        m_SilenceTimer = 0f;
        m_BargeInTimer = 0f;

        //关闭实时对话也停止 Agent Loop，撤销待 tick
        if (m_ChatSample != null) m_ChatSample.StopAgentLoop();
        InvalidateTentativeEou("DisableRealtimeMode");

        PrintLog("Loss->实时对话已关闭");
        UpdateRealtimeBtnLabel();
    }

    private void UpdateRealtimeBtnLabel()
    {
        if (m_RealtimeBtnLabel != null)
        {
            m_RealtimeBtnLabel.text = m_AwakeState ? m_LabelOn : m_LabelOff;
        }
    }
    /// <summary>
    /// 开始唤醒监听
    /// </summary>
    private void StartVoiceListening()
    {
        m_ListeningState = true;
        m_VoiceAWake.StartRecognizer();
        PrintLog("开始->识别唤醒关键词");
 
    }

    /// <summary>
    /// 停止唤醒监听
    /// </summary>
    private void StopVoiceListening()
    {
        m_ListeningState = false;
        m_VoiceAWake.StopRecognizer();
        PrintLog("结束->唤醒关键词识别");
        //StartCoroutine(WaitAndStopListen());
    }
 
    private IEnumerator WaitAndStopListen()
    {
        yield return new WaitForSeconds(1);
        m_ListeningState = false;
    }

    /// <summary>
    /// 开始监听说话声音
    /// </summary>
    private void StartRecording()
    {
        m_SilenceTimer = 0.0f; // 重置静默计时器
        m_IsRecording = true;
        PrintLog("正在录制对话...");

        //用户主动开口——通知 agent loop：撤销待 tick、清零连续 AI 轮次计数。
        //即将到来的用户文本会自动触发新一轮 LLM 调用(走 SendData → PrepareUserTurn → StartStreaming)。
        if (m_ChatSample != null) m_ChatSample.NotifyUserStartedSpeaking();

        //★ 关键：不再End/Start mic！老逻辑那一刻会丢掉触发帧的音节
        //  ("换个话题吧"被识别成"个话题吧"就是这个原因)。
        //  现在mic从应用启动起就一直跑(loop=true 30s ringbuffer)，
        //  这里只需在ring buffer里标个起始位置——并往回拨pre-roll把
        //  VAD滞后期间已经被录到、但还没触发的开头音节抢回来。
        if (!Microphone.IsRecording(m_MicrophoneName))
        {
            //兜底：mic意外停了就重启(应该不会走到这里)
            m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);
        }

        int curPos = Microphone.GetPosition(m_MicrophoneName);
        int totalSamples = m_RecordedClip != null ? m_RecordedClip.samples : 16000 * 30;
        int preRollSamples = Mathf.Clamp(
            Mathf.RoundToInt(m_RecordingPreRollSeconds * 16000f),
            0,
            Mathf.Max(0, totalSamples - 1));
        int startPos = curPos - preRollSamples;
        if (startPos < 0) startPos += totalSamples;  //ring buffer wrap
        m_RecordingStartPos = startPos;
    }
    /// <summary>
    /// 结束说话
    /// </summary>
    private void StopRecording()
    {
        m_IsRecording = false;

        PrintLog("会话录制结束...");

        //★ mic不再End/Start：保持continuous loop=true运行(StartRecording时已用m_RecordingStartPos标好起点)。
        //  从ring buffer里截出[m_RecordingStartPos, currentPos)给ASR——这段就是用户的整段发言
        //  (含开头pre-roll，避免首音节被切掉)。老逻辑那一刻End/Start会丢触发帧的音节。
        int curPos = Microphone.GetPosition(m_MicrophoneName);
        AudioClip toSend = (m_RecordingStartPos >= 0)
            ? SnapshotFromBuffer(m_RecordingStartPos, curPos)
            : null;
        m_RecordingStartPos = -1;

        //兜底：mic若被外部停掉就重启，确保后续idle期VAD/barge-in仍有数据
        if (!Microphone.IsRecording(m_MicrophoneName))
        {
            m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);
        }
        //AI说话期间不锁定——VAD要靠m_BargeInTimer工作，m_LockState只在录用户正经发言时用
        m_LockState = false;

        //把截好的clip送给ChatSample做ASR/LLM/TTS
        if (m_ChatSample != null)
        {
            //EOU锚点：必须在AcceptClip之前调用，ChatSample的DealingTextCallback/StartStreaming
            //会用这个时间戳算ASR延迟和"EOU→首音"总延迟。
            m_ChatSample.MarkEOU();
            if (m_LogTimings)
            {
                float clipLen = toSend != null ? toSend.length : 0f;
                Debug.Log($"[Timing] EOU 触发 (用户停说) — clip长度 {clipLen:F2}s, 已发送ASR");
            }
            m_ChatSample.AcceptClip(toSend);
        }
    }

    /// <summary>
    /// 从continuous loop=true buffer里截一段[startPos, endPos)做新clip。
    /// loop=true ringbuffer会在30s处wrap到0——所以endPos可能小于startPos。
    /// 跨边界时分两段拷贝再拼接，避免GetData拿到错乱的数据。
    /// 返回null时调用方应跳过本次snapshot。
    /// </summary>
    private AudioClip SnapshotFromBuffer(int startPos, int endPos)
    {
        if (m_RecordedClip == null) return null;
        int total = m_RecordedClip.samples;
        if (total <= 0) return null;
        if (startPos < 0 || startPos >= total) return null;
        if (endPos < 0 || endPos >= total) return null;

        int channels = m_RecordedClip.channels;
        int frequency = m_RecordedClip.frequency;

        //计算实际样本数。endPos == startPos当作"刚开始录还没录到"，跳过。
        int len = (endPos >= startPos) ? (endPos - startPos) : (total - startPos + endPos);
        if (len <= 0) return null;

        float[] data = new float[len * channels];
        if (endPos >= startPos)
        {
            //单段拷贝
            m_RecordedClip.GetData(data, startPos);
        }
        else
        {
            //跨ring边界：先[startPos, total) 再 [0, endPos)
            int firstLen = total - startPos;
            int secondLen = endPos;

            float[] first = new float[firstLen * channels];
            m_RecordedClip.GetData(first, startPos);
            System.Array.Copy(first, 0, data, 0, first.Length);

            if (secondLen > 0)
            {
                float[] second = new float[secondLen * channels];
                m_RecordedClip.GetData(second, 0);
                System.Array.Copy(second, 0, data, first.Length, second.Length);
            }
        }

        AudioClip clip = AudioClip.Create("rt_snapshot", len, channels, frequency, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>
    /// 兜底：保证mic处于监听态。新流程下StopRecording已经会立刻重启mic，
    /// 所以多数情况下这里只是确认。仍保留以应对Microphone被外部停掉的场景。
    /// </summary>
    public void ReStartRecord()
    {
        if (!Microphone.IsRecording(m_MicrophoneName))
        {
            m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);
        }
        m_LockState = false;
    }

    /// <summary>
    /// 对话结束回调，启动麦克风检测
    /// </summary>
    private void SpeachDoneCallBack()
    {
        ReStartRecord();
    }

    [SerializeField] private Text m_PrintText;
    /// <summary>
    /// 打印日志。m_PrintText未配置时退回Console，避免NPE阻塞toggle/状态切换。
    /// </summary>
    /// <param name="_log"></param>
    private void PrintLog(string _log)
    {
        if (m_PrintText != null) m_PrintText.text = _log;
        else Debug.Log("[RTSpeech] " + _log);
    }

    #region Tentative-EOU — 短沉默+ASR尾部判定

    /// <summary>
    /// 沉默达到m_TentativeEouSilence时调用：
    /// 1) 截当前正在录的clip(从start到now的位置)
    /// 2) 异步发预测ASR——回调里看尾部是否"说完了"再决定提前EOU还是兜底等3.5s
    /// </summary>
    private void TryFireTentativeEou()
    {
        if (m_ChatSample == null) return;
        if (m_RecordedClip == null) return;
        if (m_RecordingStartPos < 0) return;  //不在录用户发言

        //从ring buffer截[m_RecordingStartPos, curPos)——和StopRecording截法一致，
        //预测ASR看到的尾部样本和最终ASR看到的尾部样本完全相同，避免不一致引起的误判
        int curPos = Microphone.GetPosition(m_MicrophoneName);
        AudioClip snapshot = SnapshotFromBuffer(m_RecordingStartPos, curPos);
        if (snapshot == null) return;

        m_TentativeFired = true;
        m_TentativePreviewInFlight = true;
        m_TentativeSeq++;
        int seqAtFire = m_TentativeSeq;
        m_TentativePreviewSentTime = Time.realtimeSinceStartup;

        if (m_LogTentativeEou)
            Debug.Log($"[T-EOU] 派发预测ASR (seq={seqAtFire}, 沉默{m_SilenceTimer:F2}s, clip={snapshot.length:F2}s)");

        m_ChatSample.PreviewASR(snapshot, (text) => OnPreviewAsrResult(seqAtFire, text));
    }

    /// <summary>
    /// 预测ASR回包。可能在以下任一时刻到达：
    /// - 用户仍在沉默中(m_IsRecording=true) → 进入分类器决定确认/继续等
    /// - 用户已重新开口 → seq不匹配，丢弃
    /// - 3.5s硬规则已触发 → seq不匹配 OR m_IsRecording=false，丢弃
    /// </summary>
    private void OnPreviewAsrResult(int seqAtFire, string text)
    {
        m_TentativePreviewInFlight = false;

        //过期判定：用户开口/3.5s兜底已经使Invalidate把seq推进了
        if (seqAtFire != m_TentativeSeq)
        {
            if (m_LogTentativeEou)
                Debug.Log($"[T-EOU] 预测过期，丢弃 (seq={seqAtFire} ≠ {m_TentativeSeq}): \"{text}\"");
            return;
        }
        if (!m_IsRecording)
        {
            //极少：seq还匹配但StopRecording已走完——保险起见也丢弃
            if (m_LogTentativeEou)
                Debug.Log($"[T-EOU] 已不在录制状态，丢弃: \"{text}\"");
            return;
        }

        float roundTrip = Time.realtimeSinceStartup - m_TentativePreviewSentTime;
        string cls = ClassifyEnding(text);

        if (m_LogTentativeEou)
            Debug.Log($"[T-EOU] 预测回包 (seq={seqAtFire}, RT={roundTrip:F2}s, cls={cls}): \"{text}\"");

        if (cls == "complete")
        {
            //收尾很明确——直接确认EOU，省掉剩下的(3.5-0.6)≈2.9s
            ConfirmEouFromPreview(text);
        }
        //incomplete / ambiguous：保持m_TentativeFired=true，不再发预测；
        //不再尝试 mid-utterance 搭腔（路 A：彻底放弃 mid-utterance backchannel）。
        //等 m_RecordingTimeLimit 硬规则兜底，或用户重新开口走 Invalidate 路径。
        //更长沉默会由 Silence 事件兜底 (路 C)。
    }

    /// <summary>
    /// 提前确认EOU——复用预测ASR的文本(不再调一次SpeechToText)，
    /// 重启mic保持后续监听，直接把文本推进LLM链路。
    /// </summary>
    private void ConfirmEouFromPreview(string text)
    {
        if (!m_IsRecording) return;
        m_IsRecording = false;
        m_RecordingStartPos = -1;

        //★ mic保持continuous loop=true运行，不再End/Start——文本已经在手，clip角色完成使命；
        //  保留ring buffer持续监听，下一轮VAD/barge-in才能立刻检测到用户开口。
        if (!Microphone.IsRecording(m_MicrophoneName))
        {
            m_RecordedClip = Microphone.Start(m_MicrophoneName, true, 30, 16000);
        }
        m_LockState = false;

        if (m_ChatSample != null)
        {
            //EOU锚点：保持和StopRecording一致的语义，方便ASR/LLM/TTS阶段延迟统计
            m_ChatSample.MarkEOU();
            if (m_LogTimings)
            {
                float saved = m_RecordingTimeLimit - m_TentativeEouSilence;
                Debug.Log($"[Timing] T-EOU 提前确认 — 文本=\"{text}\", 节省≈{saved:F1}s");
            }
            //跳过再次SpeechToText，直接送文本进LLM
            m_ChatSample.AcceptText(text);
        }

        //清tentative状态。后续轮次重新计数
        m_TentativeFired = false;
        m_TentativeSeq++;
        PrintLog("会话录制结束(T-EOU)...");
    }

    /// <summary>
    /// 用户重新开口或3.5s硬规则触发时调用——
    /// 推进seq让任何还在飞的预测ASR回包失效，同时清tentative标志。
    /// </summary>
    private void InvalidateTentativeEou(string reason)
    {
        if (m_LogTentativeEou && (m_TentativeFired || m_TentativePreviewInFlight))
            Debug.Log($"[T-EOU] 失效 ({reason}, seq{m_TentativeSeq}→{m_TentativeSeq + 1})");
        m_TentativeFired = false;
        m_TentativeSeq++;
        //m_TentativePreviewInFlight 不在这里清——让回包按seq判老化即可，
        //避免新一轮预测立刻被以为"没在飞"而重发
    }

    /// <summary>
    /// 分类ASR文本尾部是"说完"还是"半句话"。
    /// 已经经过了m_TentativeEouSilence(默认0.6s)的沉默——能走到这一步的尾部本身就是停顿位，
    /// 所以判定可以适度激进：只要尾部是终止性标点/语气词就算complete。
    /// 返回 "complete" / "incomplete" / "ambiguous"。
    /// </summary>
    private string ClassifyEnding(string text)
    {
        if (string.IsNullOrEmpty(text)) return "ambiguous";

        //剥掉SenseVoice注入的[情绪:.. 事件:..]前缀，只看真正的转写内容
        string body = text;
        if (body.StartsWith("["))
        {
            int rb = body.IndexOf(']');
            if (rb > 0 && rb + 1 < body.Length) body = body.Substring(rb + 1).TrimStart();
        }
        body = body.TrimEnd(' ', '\t', '\n', '\r', '"', '\'', ')', '）', '」', '』');
        if (string.IsNullOrEmpty(body)) return "ambiguous";

        //—— 1) 先剥掉尾部标点。SenseVoice在"而且"、"就是说"这种半句话后面照样会自动补上句号，
        //  导致直接见到句号就判complete会把"而且。"误判为说完。
        //  - 终结性标点(。！？等)：剥掉后记下hadTerminalPunct，留作没匹配到markers时的兜底信号
        //  - 中段标点(，、,)：纯粹剥掉(SenseVoice的句中停顿提示，对完结性无意义)
        bool hadTerminalPunct = false;
        while (body.Length > 0)
        {
            char c = body[body.Length - 1];
            bool isTerminal = (c == '。' || c == '！' || c == '？'
                            || c == '.' || c == '!' || c == '?'
                            || c == '~' || c == '～' || c == '…');
            bool isMid = (c == ',' || c == '，' || c == '、');
            if (isTerminal)
            {
                hadTerminalPunct = true;
                body = body.Substring(0, body.Length - 1);
            }
            else if (isMid)
            {
                body = body.Substring(0, body.Length - 1);
            }
            else break;
        }
        body = body.TrimEnd(' ', '\t', '\n', '\r', '"', '\'', ')', '）', '」', '』');
        if (string.IsNullOrEmpty(body)) return hadTerminalPunct ? "complete" : "ambiguous";

        //—— 2) 半句话信号优先：尾部挂着结构性连接词/填充词 ——
        //  即使ASR给加了"。"，"而且。" / "就是说，"这种依然属于半句话。
        //  必须先于terminalPunct判定，否则就会被1)的兜底cover掉。
        string[] incompleteMarkers = new string[]
        {
            //中文
            "就是说", "就是", "然后", "而且", "不过", "但是", "所以", "那个", "这个",
            "因为", "如果", "虽然", "嗯", "呃", "啊那", "那么",
            //日文
            "えっと", "あの", "その", "で", "それで", "つまり",
            "ですが", "けれど", "けれども", "けど", "という", "って",
            "から", "ので", "のに", "たら", "れば",
        };
        for (int i = 0; i < incompleteMarkers.Length; i++)
        {
            if (body.EndsWith(incompleteMarkers[i])) return "incomplete";
        }

        //—— 3) 没命中半句话markers，但本来有终结性标点：判complete ——
        if (hadTerminalPunct) return "complete";

        //—— 4) 终结性语气词：在已经停顿0.6s的语境下是较强的"说完"信号 ——
        string[] terminalParticles = new string[]
        {
            //中文
            "了", "啦", "吧", "呢", "吗", "嘛", "哦", "哟", "呀",
            //日文
            "だ", "よ", "ね", "の", "わ", "さ",
            "です", "ですよ", "ですね", "ですか", "でしょう",
            "ます", "ました", "ません", "でした",
        };
        for (int i = 0; i < terminalParticles.Length; i++)
        {
            if (body.EndsWith(terminalParticles[i])) return "complete";
        }

        //—— 5) 兜底：让3.5s硬规则决定 ——
        return "ambiguous";
    }

    #endregion

}
