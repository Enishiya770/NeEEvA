using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;
/// <summary>
/// 麦克风实时聊天
/// </summary>
public partial class RTSpeechHandler : MonoBehaviour
{
    /// <summary>
    /// 麦克风名称。显式指定时始终使用该设备；留空时桌面和 XR 都使用
    /// 系统默认输入，不根据设备品牌猜测。VR/PTT 复用同一个连续输入。
    /// </summary>
    public string m_MicrophoneName = null;
    private string m_ActiveMicrophoneName;
    private bool m_HasMicrophoneSelection;
    private bool m_MicrophoneSelectionXr;
    private string m_MicrophoneSelectionSetting;
    private bool m_HasLoggedMicrophoneSelection;
    private string m_LastLoggedMicrophoneName;
    private bool m_LastLoggedMicrophoneXr;
    private NeEEvA.Player.PlayerCameraController m_MicrophoneCameraController;
    private readonly List<UnityEngine.XR.XRDisplaySubsystem> m_MicrophoneXrDisplays =
        new List<UnityEngine.XR.XRDisplaySubsystem>();
    /// <summary>
    /// 音量大于这个值，就开始录制
    /// </summary>
    public float m_SilenceThreshold = 0.01f;
    /// <summary>
    /// 普通语音的硬收束兜底。短停顿仍由 tentative EOU + final ASR 保护；
    /// 这里不再承担等待长自然停顿的职责，否则任何未被 tentative 提前确认的
    /// 普通轮次都会白等数秒。歌唱模式仍单独使用 m_SingingEouSilence。
    /// </summary>
    [Header("设置几秒没声音，就停止录制")]
    public float m_RecordingTimeLimit = 1.5f;

    /// <summary>
    /// 录音"预卷"：每次StartRecording时，把起始位置往回拨这么多秒，
    /// 这样VAD触发那一刻之前(尤其是触发那个音节本身的前半部分)的音频也会被一起送给ASR。
    /// 没有这个，"换个话题吧"就经常被识别成"个话题吧"——开头的"换"字音节
    /// 因为VAD的RMS低通需要约100ms才把包络抬到阈值之上、当时已经被错过。
    /// 0.25s ≈ 4000 samples @ 16kHz，覆盖VAD滞后(~100ms)+一帧裕量足够。
    /// </summary>
    [Header("录音预卷时长(秒) — 防丢首音节")]
    public float m_RecordingPreRollSeconds = 0.25f;

    [Header("Tentative-EOU — 把普通语音的硬等待压成'短沉默+ASR尾部判定'")]
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
    [Tooltip("预测文本看似完整后，至少持续沉默到这个时长才真正结束本轮；期间重新开口会继续同一轮。")]
    [Range(0.8f, 2.5f)] public float m_TentativeEouConfirmSilence = 1.2f;
    /// <summary>
    /// 兜底：临时EOU失败/不确定时，仍走m_RecordingTimeLimit；这个开关也能把整个机制关掉。
    /// </summary>
    public bool m_LogTentativeEou = true;

    [Header("歌唱模式 — 独立停唱判定")]
    [Tooltip("流式音高或短探测确认歌唱后，禁用普通句尾预测，避免把换气误当成说完。")]
    public bool m_EnableSingingMode = true;
    [Range(0.4f, 0.9f)] public float m_SingingProbabilityThreshold = 0.58f;
    [Tooltip("歌唱/哼唱持续安静多久才提交整段。")]
    [Range(1.2f, 3.5f)] public float m_SingingEouSilence = 1.8f;
    [Tooltip("麦克风循环缓冲长度；应覆盖一次希望分析的演唱片段。")]
    [Range(30, 120)] public int m_MicrophoneBufferSeconds = 60;

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
    [Header("自适应语音阈值 — 长时间底噪 + 开口/停说迟滞")]
    [Tooltip("最近多少秒可信空闲音频参与底噪统计。角色播放、回声保护和已确认人声不会进入样本。")]
    [Range(8f, 60f)] public float m_AmbientWindowSeconds = 24f;
    [Tooltip("底噪采用窗口内较低分位数，避免敲击、咳嗽和短暂说话把环境基线抬高。")]
    [Range(0.10f, 0.50f)] public float m_AmbientFloorQuantile = 0.30f;
    [Tooltip("空闲期采样间隔。采用长窗口低分位数，因此会接纳尚未确认的人声/音乐候选；" +
             "一旦VAD确认真人开口，会回滚最近样本，避免把用户声音学成底噪。")]
    [Range(0.10f, 1f)] public float m_AmbientSampleIntervalSeconds = 0.20f;
    [Tooltip("VAD确认真人开口后，从底噪窗口撤回最近多少秒样本。覆盖VAD探测和起音预卷。")]
    [Range(1f, 4f)] public float m_AmbientSpeechRollbackSeconds = 2.0f;
    [Tooltip("开口候选阈值相对底噪的倍数；后面仍有神经VAD确认，不直接等于开始录音。")]
    [Range(1.4f, 4f)] public float m_AmbientStartMultiplier = 2.50f;
    [Tooltip("停说阈值相对底噪的倍数。低于开口倍数形成迟滞，保留轻声和歌声渐弱尾音。")]
    [Range(1.05f, 2f)] public float m_AmbientEouMultiplier = 1.25f;
    [Tooltip("安静环境允许的最低开口候选阈值。")]
    [Range(0.001f, 0.02f)] public float m_QuietStartThreshold = 0.004f;
    [Tooltip("安静环境允许的最低停说阈值。")]
    [Range(0.001f, 0.02f)] public float m_QuietEouThreshold = 0.003f;
    [Tooltip("极嘈杂环境中的保护上限；超过后应报告输入不确定，而不是无限抬高。")]
    [Range(0.015f, 0.10f)] public float m_MaxAdaptiveStartThreshold = 0.05f;
    [SerializeField] private float m_AmbientRmsFloor = 0.01f;
    [Tooltip("低电平参考线，不裁剪音频；低于它的真实新转写和有声学支持的哼唱仍能延续采集。")]
    [Range(0.004f, 0.03f)] public float m_LowLevelReference = 0.01f;
    [SerializeField] private float m_AmbientRmsUpper = 0.005f;
    [Tooltip("空闲底噪向上跟随速度；较慢可避免用户刚开口时把人声学成底噪。")]
    [Range(0.05f, 2f)] public float m_AmbientFloorRiseRate = 0.40f;
    [Tooltip("环境变安静时底噪向下跟随速度。")]
    [Range(0.2f, 6f)] public float m_AmbientFloorFallRate = 2.0f;
    private readonly List<float> m_AmbientRmsSamples = new List<float>();
    private readonly List<float> m_AmbientRmsSampleTimes = new List<float>();
    private float m_NextAmbientSampleTime = 0f;
    private bool m_AmbientNoiseCalibrated = false;
    private float m_LastAmbientCalibrationLogTime = -999f;
    private float m_LastLoggedAmbientFloor = -1f;
    private float m_AmbientHandoffHoldUntil;
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

    [Header("神经 VAD — 正式启动录音前确认人声")]
    [Tooltip("开启后，RMS 只作为廉价候选触发；FSMN-VAD 确认是人声后才真正开始录音。")]
    public bool m_EnableNeuralVad = true;
    [SerializeField] private SenseVoiceSpeechToText m_NeuralVadClient;
    [Tooltip("每次送给 VAD 的最近音频长度。短了容易漏首音，长了会增加少量传输开销。")]
    public float m_NeuralVadProbeSeconds = 0.5f;
    [Tooltip("短探针连续否定但 RMS 仍持续时，自动录音兜底的等待时长；完整 ASR 仍是最终人声判定。")]
    public float m_NeuralVadFallbackSeconds = 0.8f;
    [Tooltip("角色说话时用于声纹判定的探测窗口。CAM++ 需要比普通 VAD 更长的语音。")]
    public float m_BargeInSpeakerProbeSeconds = 1.0f;
    [Tooltip("只有已知真人身份达到此置信度才允许打断角色。")]
    [Range(-1f, 1f)] public float m_MinBargeInSpeakerConfidence = 0.50f;
    [Tooltip("开启后，无法确认身份的人声也允许打断；外放场景建议关闭。")]
    public bool m_AllowUnknownBargeIn = false;
    [Header("AEC - 外放回声消除（仅用于打断判定）")]
    [Tooltip("用角色实际播放的波形作为反向参考，在声纹判定前抵消扬声器回声。")]
    public bool m_EnableManagedAec = true;
    [Tooltip("扬声器到麦克风的最大延迟搜索范围。蓝牙设备可适当增大。")]
    [Range(50f, 800f)] public float m_AecMaxDelayMs = 450f;
    [Tooltip("播放参考与麦克风片段至少达到该相关度才执行抵消，过低会误伤真人语音。")]
    [Range(0f, 1f)] public float m_AecMinCorrelation = 0.20f;
    [Tooltip("估计出的回声分量抵消强度。保留少量余量可减少双讲时对真人声音的影响。")]
    [Range(0f, 1f)] public float m_AecStrength = 0.90f;
    [Tooltip("原始麦克风音频与 AI_SELF 达到该相似度时优先按角色回声处理。")]
    [Range(0f, 1f)] public float m_MinRawSelfConfidence = 0.52f;
    [Tooltip("只有 AEC 后仍保留足够能量才继续做真人声纹判断；过低说明主要是回声。")]
    [Range(0f, 1f)] public float m_MinAecHumanResidualRatio = 0.55f;
    [Tooltip("原始音频未确认身份时，AEC 残音匹配已确认真人所需的更高置信度。")]
    [Range(0f, 1f)] public float m_MinAecResidualSpeakerConfidence = 0.60f;
    [Tooltip("AEC 不可用时，只有高置信度的已确认真人才能触发打断。")]
    [Range(0f, 1f)] public float m_MinNoAecBargeInSpeakerConfidence = 0.75f;
    [Tooltip("AEC 不可用时，真人声纹分数必须至少比 AI_SELF 相似度高出该值。")]
    [Range(0f, 0.5f)] public float m_MinNoAecHumanOverSelfMargin = 0.10f;
    [Tooltip("真实扬声器播放结束后继续屏蔽麦克风的时间，用于覆盖房间回声和设备缓冲尾音。")]
    [Range(0.2f, 3f)] public float m_PostPlaybackEchoGuardSeconds = 1.2f;
    [Tooltip("VAD 判定为非人声后，持续有声音时多久再探测一次。")]
    public float m_NeuralVadRetrySeconds = 0.2f;
    [Tooltip("AI 播放期间声纹感知打断的最短重试间隔。适当放大可避免外放回声持续占用 ASR/主线程。")]
    public float m_BargeInVadRetrySeconds = 0.45f;
    [Tooltip("AEC 判断为强相关外放回声时，在本地直接拦截所需的最低相关度。")]
    [Range(0f, 1f)] public float m_LocalEchoVetoCorrelation = 0.35f;
    [Tooltip("AEC 后残余能量低于原始输入的该比例时，结合高相关度按纯外放回声处理。")]
    [Range(0f, 1f)] public float m_LocalEchoVetoResidualRatio = 0.55f;
    [Tooltip("候选声音消失多久后，放弃本次待确认的录音起点。")]
    public float m_NeuralVadCandidateResetSeconds = 0.5f;

    private bool m_NeuralVadProbeInFlight = false;
    private float m_NextNeuralVadProbeTime = 0f;
    private float m_LastNeuralVadCandidateTime = -1f;
    private float m_NeuralVadCandidateSince = -1f;
    private int m_PendingSpeechStartPos = -1;
    private int m_NeuralVadSequence = 0;
    /// <summary>
    /// barge-in计时器从0刚刚抬起来的时间戳。Interrupt()触发时打"用户连续说话Xs"。
    /// </summary>
    private float m_BargeInWindowStartTime = 0f;
    private bool m_CurrentRecordingAllowsSpeakerLearning = true;
    private bool m_LikelySinging = false;
    //这只保护歌唱式停顿，不向LLM宣称“用户一定在唱”。连续旋律证据即使和
    //临时语义草稿冲突，也应该阻止0.8秒预测ASR把一次换气当成整轮结束。
    private bool m_MelodicEouProtectionActive = false;
    private int m_MelodicEouEvidenceFrames = 0;
    private int m_MelodicEouLowFrames = 0;
    private int m_LastMelodicEouEvidenceAudioMs = -1;
    //本次录音是不是"语音VAD本来拒绝了、靠哼唱豁免捞回来的"。
    //与 m_LikelySinging 的区别：那个被 m_EnableSingingMode 门控，关掉歌唱模式就丢了信号。
    //服务端 /vad 的 is_singing 恰好等价于 singing_override，正是这个含义。
    private bool m_RecordingRescuedByTonalOverride = false;
    private float m_CurrentSingingProbability = 0f;

    [Header("Agent感知 — 环境扰动通知 (Agent Loop 用)")]
    [Tooltip("非语音 spike 的 RMS 阈值。用来识别咳嗽/翻身/叹息/键盘声等。" +
        "高于这个值就 ping 一下 ChatSample.OnEnvironmentSpike，让 agent loop 决定要不要把下次 tick 拉前。\n" +
        "原为 0.005，实测触发的 11 次 spike 全部落在 0.0050-0.0068，紧贴阈值——那是房间" +
        "本底噪声在阈值线上下抖，不是真实动静。抬到 0.012 才能只捕捉到确实发生了什么。")]
    public float m_RmsSpikeThreshold = 0.012f;
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

    //最终 ASR 不能只在 EOU 时回看固定长度的 microphone ring buffer：录音超过
    //m_MicrophoneBufferSeconds 后开头必然被覆盖。录制期间每 0.5s 把新增 PCM 取出
    //并按块保存，EOU 再合成完整 AudioClip；内存随真实录音线性增长，不设歌曲时长上限。
    private const float k_RecordingCaptureIntervalSeconds = 0.5f;
    private readonly List<float[]> m_RecordingPcmChunks = new List<float[]>();
    // Reversible physical handoff: a breath/tail before any reply must not become
    // an unrelated one-character turn. Own the chunks, not a caller-owned AudioClip.
    private readonly List<float[]> m_UnheardCapture = new List<float[]>();
    private float m_UnheardCaptureClosedAt = -1f, m_RecordingCapturedAt;
    private float m_UnheardCapturedAt;
    private int m_UnheardCaptureSessionSerial;
    private int m_LastDeferredRmsContinuationCapture = -1;
    private int m_RecordingFirstCaptureSerial;
    private string m_RecordingFirstCloseUtc;
    private string m_UnheardFirstCloseUtc, m_UnheardCloseUtc;
    private int m_UnheardFirstCaptureSerial;
    private int m_UnheardEndPos, m_UnheardFrames, m_UnheardChannels, m_UnheardFrequency, m_UnheardVoiceRevision;
    private AudioClip m_UnheardMic;
    private AudioClip m_RejectedContinuationMic;
    private int m_RejectedContinuationEndPos = -1;
    private float m_RejectedContinuationClosedAt = -1f;
    private int m_RecordingCaptureLastPos = -1;
    private int m_RecordingCapturedFrames = 0;
    private int m_RecordingCaptureChannels = 1;
    private int m_RecordingCaptureFrequency = 16000;
    private float m_NextRecordingCaptureTime = 0f;

    // Manual push-to-talk owns the same PCM chunks as the VAD path.  It never
    // starts a second Microphone session, so XR/desktop PTT and realtime VAD
    // cannot silently select different input devices.
    [Header("按住说话 — 共享实时麦克风环形缓冲")]
    [Tooltip("按住说话允许的最长时长；到达后自动提交当前片段并结束本次捕获。")]
    [Range(1f, 120f)] public float m_ManualCaptureMaxSeconds = 30f;
    [Tooltip("按住说话开始时从环形缓冲回收的首音节预卷时长。")]
    [Range(0f, 1f)] public float m_ManualCapturePreRollSeconds = 0.15f;
    [Tooltip("控制器抖动或极短点击产生的最短音频；低于此时长会丢弃空片段。")]
    [Range(0f, 0.5f)] public float m_ManualCaptureMinSeconds = 0.05f;
    private bool m_ManualCaptureActive = false;
    private int m_ManualCaptureInputOwner = -1;
    private float m_ManualCaptureStartedAt = -1f;
    private int m_ManualCaptureStartPos = -1;
    // Keep automatic VAD quiet for a short settle window when realtime mode
    // is enabled; stale mic/ring-buffer state must not become a user turn.
    private float m_AutoListeningBlockedUntil = -1f;
    private float m_NextMicrophoneStartTime = 0f;
    private float m_NextMicrophoneWarningTime = 0f;
    private int m_LastMicrophonePosition = -1;
    private bool m_MicrophoneHasWrapped = false;

    // —— Tentative-EOU 运行时状态 ——
    /// <summary>当前正在做预测ASR(送了clip在等回包)。第二次沉默到点不重发——避免刷请求</summary>
    private bool m_TentativePreviewInFlight = false;
    /// <summary>临时EOU机制的轮次序号。每次发预测ASR递增，回调用它判断是否过期</summary>
    private int m_TentativeSeq = 0;
    /// <summary>临时EOU已经派发但还没确认。普通语音硬规则触发或用户重新开口时清掉</summary>
    private bool m_TentativeFired = false;
    /// <summary>预测ASR派发时的时间戳——用来日志显示"省了多少秒"</summary>
    private float m_TentativePreviewSentTime = 0f;
    /// <summary>
    /// 同一录音至多派发一次完整快照预览。用户重新开口会使第一次预览失效，
    /// 但不应在同一 capture 上取消并重新上传整段音频；最终 ASR 会负责收口。
    /// </summary>
    private bool m_TentativeFullPreviewAttempted = false;
    private bool m_TentativeFullPreviewRetrySuppressed = false;
    private bool m_TentativeCompletePending = false;
    private string m_TentativeCompleteText = "";
    private bool m_TentativeUsedFullAsr = false;
    private bool m_TentativeFullAsrResultReady = false;
    private int m_TentativeFullAsrActivityRevision = -1;
    private float m_TentativeFullAsrSoundAt = -1f;
    private string m_TentativeFullAsrText = "";
    private string m_TentativeFullAsrTranscriptKey = "";
    private string m_TentativeFullAsrRawText = "";
    private bool m_AwaitingTentativeFinal = false;
    private int m_AwaitingTentativeSeq = -1;
    private AudioClip m_AwaitingTentativeFinalClip = null;
    private bool m_AwaitingTentativeAllowSpeakerLearning = true;
    private bool m_AwaitingTentativeStreamingExit = false;
    private Coroutine m_AwaitingTentativeFallbackCoroutine = null;
    [Tooltip("硬EOU已经到达但预测ASR仍在飞时，最多再等多久复用它；超时才重新提交完整音频。")]
    [Range(3f, 30f)] public float m_TentativeFinalReuseTimeoutSeconds = 15f;

    [Header("持续背景声下的语义 EOU")]
    [Tooltip("转写多久没有实质变化后，把持续声学活动、底噪和文字一起交给现有角色LLM判断轮次是否已经结束。")]
    [Range(1.5f, 8f)] public float m_StalledTranscriptReviewSeconds = 2.8f;
    [Tooltip("一次LLM边界判断后，若她选择继续听，至少多久再复核。")]
    [Range(2f, 15f)] public float m_StalledTranscriptReviewCooldown = 5.0f;
    [Tooltip("仅用于旧 complete（确信已结束）。ask_user/take_turn 是角色主动选择，不受此门槛限制。")]
    [Range(0.55f, 0.98f)] public float m_StalledTurnDecisionMinConfidence = 0.78f;
    [Tooltip("相对PCM电平下降多少dB时提供提前复核证据；不直接结束录音。")]
    [Range(6f, 24f)] public float m_TurnBoundaryDropDb = 10f;
    [Tooltip("电平下降需保持多久才触发提前复核。")]
    [Range(0.2f, 1f)] public float m_TurnBoundaryDropHoldSeconds = 0.35f;
    [Tooltip("快速证据出现后所需的文字稳定时间，与下降保持并行计算，不串行相加。")]
    [Range(0.4f, 1.5f)] public float m_FastBoundaryTextSeconds = 0.65f;
    private readonly TurnBoundaryAcoustics m_BoundaryAcoustics = new TurnBoundaryAcoustics();
    private readonly TurnActivityEvidence m_TurnActivity = new TurnActivityEvidence();
    private bool m_ActivityProtocolWarningLogged;
    private float EffectiveNoiseUpper => m_AmbientNoiseCalibrated
        ? Mathf.Max(m_AmbientRmsFloor, m_AmbientRmsUpper) : Mathf.Max(0.001f, m_SilenceThreshold * 0.5f);
    private float EffectiveQuietThreshold => Mathf.Min(m_MaxAdaptiveStartThreshold,
        Mathf.Max(m_LowLevelReference, EffectiveNoiseUpper * 1.15f));
    private bool RoleChoosesBoundary => m_ChatSample != null && m_ChatSample.SupportsRoleTurnBoundary;
    private bool HasFreshStreamingCoverage => m_TurnActivity.AsrHealthy(Time.realtimeSinceStartup) &&
        m_TurnActivity.CoversRecentAudio(
            Mathf.RoundToInt(m_StreamSubmittedSeconds * 1000f),
            m_StreamPartialMaxLagMs);

    private bool HasDegradedQuietTailEvidence(float now)
    {
        if (!m_EnableStreamingRecognition || m_TurnActivity.PendingText ||
            m_TurnActivity.LevelActive || m_TurnActivity.MelodyActive ||
            m_TurnActivity.TextActivityActive)
            return false;

        float quietSeconds = m_TurnActivity.QuietSeconds(now);
        if (quietSeconds < m_DegradedAsrCloseSilence) return false;

        // CoversQuietTail proves that the ASR packet reached the quiet suffix;
        // AsrHealthy/progress checks separately cover a dead or stalled stream.
        // This deliberately does not require fresh coverage of the whole capture:
        // the original PCM remains available for deferred final ASR.
        bool streamStale = !m_TurnActivity.AsrHealthy(now) ||
            m_LastStreamProgressTime < 0f ||
            now - m_LastStreamProgressTime > m_StreamPartialMaxAgeSeconds;
        return m_TurnActivity.CoversQuietTail || streamStale;
    }
    private float m_LastStreamProgressTime = -1f;
    private float m_BoundaryCandidateTime = -1f;
    private float m_LastReviewedDropSince = -1f;
    private float m_StalledRequiredUnchangedSeconds = 0f;
    private int m_StalledReviewActivityRevision;
    private int m_StalledReviewTextRevision;
    // Backend timing survives capture boundaries. A cancelled request is only a
    // lower bound; a synchronous prepared decision is not a network sample.
    private float m_ObservedBoundarySeconds;
    private float m_LastBoundaryRemoteAttemptAt = -999f;
    private bool m_StalledReviewRemoteAllowed;
    private bool m_BoundaryRemoteTimingArmed;
    private bool m_BoundaryRequestTimingRecorded;
    private float m_StalledReviewSoundAt = -1f;
    private int m_BoundaryRenewedSoundCount;
    private int m_StalledReviewFailures;
    private string m_RecordingCloseReason = "physical-eou";
    private float m_LastBoundaryEvidenceLogTime = -1f;
    private string m_LastMeaningfulStreamText = "";
    private string m_LastMeaningfulStreamTextKey = "";
    private float m_LastMeaningfulStreamTextTime = -999f;
    private int m_LastMeaningfulStreamTextAudioMs = 0;
    private float m_NextStalledTranscriptReviewTime = 0f;
    private bool m_StalledTranscriptReviewRequested = false;
    private float m_StalledTranscriptReviewRequestTime = -999f;
    private ConversationTrace m_PendingSemanticEouTrace;
    private float m_NextCloseBlockTraceAt;
    private bool m_PendingSemanticEou = false;
    private string m_PendingSemanticEouStatus = "";
    private string m_PendingSemanticEouTextKey = "";
    private float m_PendingSemanticEouConfidence = 0f;
    private string m_PendingSemanticEouObservedMode = "";
    private float m_PendingSemanticEouModeConfidence = 0f;
    private string m_PendingSemanticEouSource = "";
    private float m_PendingSemanticDecisionAt = -1f;
    private bool m_LoggedSemanticCoverageWait;
    private float m_LastStreamingPitchStability = 0f;
    private float m_LatestStreamingSingingProbability = 0f;
    private float m_RecentStreamingSingingProbability = 0f;
    private float m_StreamingSingingProbabilityTrend = 0f;
    private float m_PreviousStreamingSingingProbability = 0f;
    private bool m_HasStreamingSingingProbability = false;
    private int m_StalledContinueDecisionCount = 0;
    private string m_LastStalledDecisionStatus = "";
    private float m_LastStalledDecisionConfidence = 0f;

    [Header("流式倾听 — partial 只用于临时理解，EOU 后仍做最终 ASR")]
    [Tooltip("复用 SenseVoice WebSocket partial；关闭后完全回落到原整段 ASR 流程。")]
    public bool m_EnableStreamingRecognition = true;
    [Tooltip("保留普通说话的稳定 partial 作为预判证据；正式 RoleDecision 仍等待完整 final ASR，避免长句回滚污染正式输入。")]
    [SerializeField] private bool m_EnableStablePartialPromotion = true;
    [Tooltip("从 microphone ring buffer 向流式服务发送新音频的间隔。")]
    [Range(0.05f, 0.5f)] public float m_StreamAudioFrameSeconds = 0.10f;
    [Tooltip("Tentative-EOU 可复用的 partial 最长回包年龄。")]
    [Range(0.3f, 2.5f)] public float m_StreamPartialMaxAgeSeconds = 1.25f;
    [Tooltip("partial 覆盖的音频比当前录音最多落后多少毫秒，超出则仍调用旧预览 ASR。实验基线为1800ms；仍受年龄、revision、activity和coverage门控约束。")]
    [Range(200, 2000)] public int m_StreamPartialMaxLagMs = 1800;
    [Tooltip("ASR流式覆盖落后时，普通语音仍有稳定静音尾部就允许提前收束。歌唱模式不使用此门控。")]
    [Range(1.2f, 3.5f)] public float m_DegradedAsrCloseSilence = 1.8f;
    [Tooltip("语义EOU在等待新鲜ASR覆盖前的最短降级等待；完整录音仍交给后台最终ASR。")]
    [Range(0.3f, 2.5f)] public float m_DegradedCoverageGraceSeconds = 0.8f;
    [Tooltip("已约定跟唱且流式确认歌唱后，提前截取开头这段交给角色做声线转换。它会在用户继续唱时后台完成，用来让真正歌声在EOU后立即开始。")]
    [Range(6f, 30f)] public float m_StreamHumBackPrefixSeconds = 20f;

    [Header("录音生命周期 watchdog")]
    [Tooltip("单次用户录音的最长保留时间。超过后仍保留已采集 PCM，并交给最终 ASR；只收束当前 capture。")]
    [Range(15f, 600f)] public float m_MaxRecordingSeconds = 120f;
    [Tooltip("流式 ASR 已启动但没有新进度时，最长等待多久后收束 capture。断流会先给一个更短的 terminal grace。")]
    [Range(2f, 60f)] public float m_AsrStreamStallSeconds = 12f;
    [Tooltip("收到 WebSocket remote-close/error 后给主线程回调和最后一帧音频留下的宽限时间。")]
    [Range(0.2f, 5f)] public float m_AsrStreamTerminalGraceSeconds = 1f;
    [Tooltip("语义 EOU 已选择结束但流式尾部没有覆盖时，最多保留 pending decision 的时间。")]
    [Range(0.5f, 10f)] public float m_CoverageWaitTimeoutSeconds = 2.5f;

    private int m_StreamLastSentPos = -1;
    private float m_StreamSubmittedSeconds;
    private float m_LastStreamAudioSubmittedAt = -1f;
    private float m_NextStreamAudioPushTime = 0f;
    private string m_LatestStreamPartial = "";
    private int m_LatestStreamPartialAudioMs = 0;
    private float m_LatestStreamPartialTime = -1f;
    // Stable transcript is kept separately from the latest (possibly revised)
    // hypothesis.  EOU may promote only this lane for ordinary speech; the
    // complete clip still goes to final ASR in parallel.
    private string m_LatestStreamStablePartial = "";
    private int m_LatestStreamStablePartialAudioMs = 0;
    private float m_LatestStreamStablePartialTime = -1f;
    private bool m_StreamHumBackPrefixOffered = false;
    private float m_StreamingStartedAt = -1f;
    private float m_StreamingTerminalAt = -1f;
    private string m_StreamingTransportState = "idle";
    private string m_StreamingTransportReason = "";
    private int m_StreamingTransportGeneration = -1;
    private bool m_StreamingPushStopped = false;
    private readonly ClosedSpeechEvidenceTracker m_ClosedSpeechTracker = new ClosedSpeechEvidenceTracker();
    public int CurrentCaptureSerial => m_NeuralVadClient != null ? m_NeuralVadClient.InputCaptureSessionSerial : 0;

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
    [SerializeField] private string m_LabelClosing = "正在完成当前对话（点击恢复）";
    private Coroutine m_GracefulDisableCoroutine;
    private bool m_GracefulDisablePending = false;

    // Presentation reads state without changing VAD, microphone or shutdown logic.
    public bool IsRealtimeEnabled { get { return m_AwakeState; } }
    public bool IsRealtimeClosing { get { return m_GracefulDisablePending; } }
    public bool IsRecording { get { return m_IsRecording; } }
    /// <summary>当前是否有一轮按住说话捕获正在进行。</summary>
    public bool IsManualCaptureActive { get { return m_ManualCaptureActive; } }
    public ChatSample ChatOwner { get { return m_ChatSample; } }

    /// <summary>
    /// 从已经运行的连续麦克风环形缓冲开始一次手动按住说话捕获。
    /// 返回 false 表示没有可用麦克风，或实时 VAD 已占用捕获状态。
    /// </summary>
    public bool BeginManualCapture()
    {
        if (m_ManualCaptureActive || m_IsRecording)
            return false;

        if (!TryEnsureMicrophone(out int currentPos))
        {
            Debug.LogWarning("[RTSpeech] PTT开始失败：没有可用的已选麦克风。");
            return false;
        }

        float preRoll = Mathf.Max(0f, m_ManualCapturePreRollSeconds);
        int preRollSamples = Mathf.RoundToInt(preRoll * Mathf.Max(1, m_RecordedClip.frequency));
        int startPos = !m_MicrophoneHasWrapped && currentPos < preRollSamples
            ? Mathf.Max(0, currentPos - preRollSamples)
            : CalculateRecordingStartPos(currentPos, preRoll);

        // Reuse the same chunk accumulator as realtime VAD.  There can only be
        // one owner because m_IsRecording and m_ManualCaptureActive are guarded
        // above, so the two paths never interleave their PCM.
        m_UnheardCapture.Clear();
        m_UnheardMic = null;
        EndStreamingRecognition();
        ResetNeuralVadGate("manual-ptt-start");
        m_SmoothedRms = 0f;
        m_SilenceTimer = 0f;
        m_BargeInTimer = 0f;
        m_RecordingStartPos = -1;
        personalLearningRecorder.Begin();
        BeginRecordingCapture(startPos, currentPos);
        m_ManualCaptureActive = true;
        m_ManualCaptureStartedAt = Time.realtimeSinceStartup;
        m_ManualCaptureStartPos = startPos;
        m_ChatSample?.NotifyManualCaptureStarted();
        m_ManualCaptureInputOwner = m_ChatSample != null ? m_ChatSample.ManualCaptureInputOwner : -1;
        if (m_LogTimings)
            Debug.Log($"[RTSpeech] PTT开始：device='{m_ActiveMicrophoneName ?? "system-default (null)"}' preRoll={preRoll:F2}s");
        return true;
    }

    /// <summary>
    /// 结束手动按住说话捕获，并把环形缓冲中实际录到的短片段提交给 ChatSample。
    /// 返回 false 表示没有活动捕获或片段无效；调用方可以安全地重复调用。
    /// </summary>
    public bool EndManualCapture()
    {
        if (!m_ManualCaptureActive)
            return false;
        int inputOwner = m_ManualCaptureInputOwner;
        m_ManualCaptureInputOwner = -1;
        try
        {
            m_ManualCaptureActive = false;
            float elapsed = m_ManualCaptureStartedAt >= 0f
                ? Time.realtimeSinceStartup - m_ManualCaptureStartedAt : 0f;
            m_ManualCaptureStartedAt = -1f;
            ResetNeuralVadGate("manual-ptt-end");
            m_SmoothedRms = 0f;
            m_SilenceTimer = 0f;
            m_BargeInTimer = 0f;
            m_RecordingStartPos = -1;

            int currentPos;
            if (!TryGetMicrophonePosition(out currentPos))
            {
                personalLearningRecorder.Seal(null);
                ResetRecordingCapture();
                m_ManualCaptureStartPos = -1;
                Debug.LogWarning("[RTSpeech] PTT结束失败：麦克风位置无效，丢弃本次片段。");
                return false;
            }

            CaptureRecordingAudio(currentPos, true);
            AudioClip clip = BuildAccumulatedRecordingClip();
            int startPos = m_ManualCaptureStartPos;
            m_ManualCaptureStartPos = -1;

            if (clip == null && startPos >= 0)
                clip = SnapshotFromBuffer(startPos, currentPos);

            SealPersonalLearningCapture(clip, false);

            float minSeconds = Mathf.Max(0f, m_ManualCaptureMinSeconds);
            if (clip == null || clip.samples <= 0 ||
                clip.length < minSeconds)
            {
                if (clip != null) Destroy(clip);
                if (m_LogTimings)
                    Debug.Log($"[RTSpeech] PTT片段过短，丢弃 ({elapsed:F2}s)");
                return false;
            }

            // AcceptClip owns the ASR/LLM/TTS handoff.  The microphone ring keeps
            // running, so releasing PTT never changes the realtime listener's mic.
            if (m_ChatSample == null)
            {
                Destroy(clip);
                Debug.LogWarning("[RTSpeech] PTT结束失败：未配置 ChatSample。");
                return false;
            }
            // Use the independent-input overload so SenseVoice discards any stale
            // realtime preview/session state before recognizing this PTT clip.
            m_ChatSample.AcceptClip(clip);
            if (m_LogTimings)
                Debug.Log($"[RTSpeech] PTT结束：提交 {clip.length:F2}s");
            return true;
        }
        finally { m_ChatSample?.NotifyManualCaptureEnded(inputOwner); }
    }
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
        m_MicrophoneCameraController = FindObjectOfType<NeEEvA.Player.PlayerCameraController>();
        if (m_NeuralVadClient == null)
        {
            m_NeuralVadClient = FindObjectOfType<SenseVoiceSpeechToText>();
        }
        if (m_NeuralVadClient != null)
            m_NeuralVadClient.OnStreamingTransportChanged += HandleStreamingTransportChanged;

        //AI回复结束回调
        if (m_ChatSample != null)
        {
            m_ChatSample.OnAISpeakDone += SpeachDoneCallBack;
            m_ChatSample.OnStableUserSingingObserved += HandleStableUserSingingObserved;
            m_ChatSample.OnStableUserSpeechObserved += HandleStableUserSpeechObserved;
            m_ChatSample.OnStalledUserTurnDecision += HandleStalledUserTurnDecision;
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

    private void OnDestroy()
    {
        // Scene unloads and component disables can happen while a controller
        // trigger is held.  Drop the chunk accumulator so the next scene cannot
        // submit stale audio from the previous one.
        CancelManualCapture();
        if (m_NeuralVadClient != null)
            m_NeuralVadClient.OnStreamingTransportChanged -= HandleStreamingTransportChanged;
        if (m_ChatSample != null)
        {
            m_ChatSample.OnAISpeakDone -= SpeachDoneCallBack;
            m_ChatSample.OnStableUserSingingObserved -= HandleStableUserSingingObserved;
            m_ChatSample.OnStableUserSpeechObserved -= HandleStableUserSpeechObserved;
            m_ChatSample.OnStalledUserTurnDecision -= HandleStalledUserTurnDecision;
        }
    }

    private void OnDisable()
    {
        // XR can be disabled before the owning ChatSample is destroyed. Do not
        // leave a manual capture armed across a temporary component disable.
        CancelManualCapture();
        personalLearningRecorder.CancelPitch();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // Losing Windows focus to SteamVR is not a release of the held XR trigger.
        // XR input release, mode exit and OnDisable still end/cancel the capture.
        if (!hasFocus && m_ManualCaptureActive && !IsMicrophoneXrActive())
            CancelManualCapture();
    }

    private void CancelManualCapture()
    {
        if (!m_ManualCaptureActive && m_ManualCaptureStartPos < 0)
            return;
        personalLearningRecorder.Seal(null);
        int inputOwner = m_ManualCaptureInputOwner;
        m_ManualCaptureInputOwner = -1;
        m_ManualCaptureActive = false;
        m_ManualCaptureStartedAt = -1f;
        m_ManualCaptureStartPos = -1;
        m_RecordingStartPos = -1;
        ResetRecordingCapture();
        ResetNeuralVadGate("manual-ptt-cancelled");
        m_SmoothedRms = 0f;
        m_SilenceTimer = 0f;
        m_BargeInTimer = 0f;
        m_ChatSample?.NotifyManualCaptureEnded(inputOwner);
    }

    private bool TryEnsureMicrophone(out int currentPos)
    {
        currentPos = -1;
        bool xrActive = IsMicrophoneXrActive();
        // Never switch underneath an in-flight capture. Re-evaluate the automatic
        // choice at the next idle boundary without overwriting the inspector setting.
        if (m_HasMicrophoneSelection && !m_IsRecording && !m_ManualCaptureActive &&
            (m_MicrophoneSelectionXr != xrActive ||
             !string.Equals(m_MicrophoneSelectionSetting, m_MicrophoneName, StringComparison.Ordinal)))
            ReleaseMicrophone();
        if (!m_HasMicrophoneSelection)
        {
            string[] devices = GetMicrophoneDevices();
            if (devices == null || devices.Length == 0)
                return false;
            m_ActiveMicrophoneName = SelectMicrophoneName(m_MicrophoneName);
            m_MicrophoneSelectionXr = xrActive;
            m_MicrophoneSelectionSetting = m_MicrophoneName;
            m_HasMicrophoneSelection = true;
            if (!m_HasLoggedMicrophoneSelection || m_LastLoggedMicrophoneXr != xrActive ||
                !string.Equals(m_LastLoggedMicrophoneName, m_ActiveMicrophoneName, StringComparison.Ordinal))
            {
                Debug.Log($"[RTSpeech/Microphone] mode={(xrActive ? "XR" : "desktop")} " +
                    $"input={m_ActiveMicrophoneName ?? "system-default (null)"} " +
                    $"selection={(string.IsNullOrWhiteSpace(m_MicrophoneName) ? "automatic" : "explicit")}");
                m_HasLoggedMicrophoneSelection = true;
                m_LastLoggedMicrophoneName = m_ActiveMicrophoneName;
                m_LastLoggedMicrophoneXr = xrActive;
            }
        }

        if (m_RecordedClip == null || !MicrophoneIsRecording(m_ActiveMicrophoneName))
        {
            if (Time.realtimeSinceStartup < m_NextMicrophoneStartTime)
                return false;
            m_NextMicrophoneStartTime = Time.realtimeSinceStartup + 0.25f;
            try
            {
                m_RecordedClip = StartMicrophone(m_ActiveMicrophoneName);
            }
            catch (Exception e)
            {
                if (m_LogTimings) Debug.LogWarning($"[RTSpeech] 启动麦克风失败: {e.Message}");
                m_RecordedClip = null;
                return false;
            }
            m_LastMicrophonePosition = -1;
            m_MicrophoneHasWrapped = false;
            if (m_RecordedClip == null)
                return false;
        }

        return TryGetMicrophonePosition(out currentPos);
    }

    private static string SelectMicrophoneName(string configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName)) return configuredName;
        // A working headset input can have a generic USB name (for example,
        // Capture Input terminal / AC Interface), while stale PICO virtual
        // inputs remain listed. Honor the user's system default in XR as well.
        // Unity's null device is the system default, not the first enumerated input.
        return null;
    }

    protected virtual bool IsMicrophoneXrActive()
    {
        if (m_MicrophoneCameraController != null)
            return m_MicrophoneCameraController.isActiveAndEnabled && m_MicrophoneCameraController.IsVRActive;
        UnityEngine.SubsystemManager.GetInstances(m_MicrophoneXrDisplays);
        foreach (var display in m_MicrophoneXrDisplays)
            if (display.running) return true;
        return false;
    }

    // Keep the native input boundary replaceable for offline lifecycle regression.
    protected virtual string[] GetMicrophoneDevices() => Microphone.devices;
    protected virtual bool MicrophoneIsRecording(string device) => Microphone.IsRecording(device);
    protected virtual int GetMicrophonePosition(string device) => Microphone.GetPosition(device);
    protected virtual AudioClip StartMicrophone(string device) =>
        Microphone.Start(device, true, Mathf.Max(1, m_MicrophoneBufferSeconds), 16000);
    protected virtual void EndMicrophone(string device) => Microphone.End(device);

    private void ReleaseMicrophone()
    {
        // A desktop manual recording also uses null. Once our clip is released,
        // the idle loop must not keep stopping that independently owned recording.
        if (m_HasMicrophoneSelection && m_RecordedClip != null && MicrophoneIsRecording(m_ActiveMicrophoneName))
            EndMicrophone(m_ActiveMicrophoneName);
        m_RecordedClip = null;
        m_HasMicrophoneSelection = false;
        m_ActiveMicrophoneName = null;
        m_NextMicrophoneStartTime = 0f;
    }

    private bool TryGetMicrophonePosition(out int currentPos)
    {
        currentPos = -1;
        if (m_RecordedClip == null || !m_HasMicrophoneSelection)
            return false;
        int total = m_RecordedClip.samples;
        if (total <= 0 || !MicrophoneIsRecording(m_ActiveMicrophoneName))
            return false;
        try
        {
            currentPos = GetMicrophonePosition(m_ActiveMicrophoneName);
        }
        catch (Exception e)
        {
            if (m_LogTimings) Debug.LogWarning($"[RTSpeech] 获取麦克风位置失败: {e.Message}");
            return false;
        }
        if (currentPos >= 0 && currentPos < total)
        {
            if (m_LastMicrophonePosition >= 0 && currentPos < m_LastMicrophonePosition)
                m_MicrophoneHasWrapped = true;
            m_LastMicrophonePosition = currentPos;
            return true;
        }
        return false;
    }

    private void Start()
    {
        // 启动录制状态检测协程。麦克风权限/设备初始化是异步的，不能在
        // Start() 中用无界 while 阻塞主线程；DetectRecording 会在设备出现后
        // 自动重试并维持同一个 loop=true 环形缓冲。
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
            DiscardRejectedUnheardCapture();
            // Automatic realtime VAD must not keep listening or uploading while
            // realtime mode is off. Explicit push-to-talk remains allowed, as
            // does the configured wake-word listener.
            if (!m_AwakeState && !m_ManualCaptureActive && !m_IsRecording &&
                m_VoiceAWake == null)
            {
                ReleaseMicrophone();
                yield return null;
                continue;
            }
            if (!m_IsRecording && m_UnheardCapture.Count > 0 &&
                !CanResumeUnheardCapture(true, Time.realtimeSinceStartup - m_UnheardCaptureClosedAt,
                    m_ChatSample != null && (m_ChatSample.IsVoiceOutputPlaying ||
                        m_ChatSample.VoiceOutputRevision != m_UnheardVoiceRevision),
                    m_UnheardMic == m_RecordedClip))
            {
                m_UnheardCapture.Clear();
                m_UnheardMic = null;
                EndStreamingRecognition();
            }
            // Evaluate capture bounds before reading the microphone position.  A
            // device that reports a frozen/invalid position must not bypass the
            // lifecycle watchdog indefinitely.
            if (m_IsRecording && TryApplyCaptureWatchdog(Time.realtimeSinceStartup))
            {
                yield return null;
                continue;
            }
            //守卫：万一mic被外部停掉(loop=false过期、设备断开、其他脚本调End)，
            //GetPosition会一直返回0/无效值，整个VAD永远不工作。检测到就重启mic。
            //loop=true保证沉默期间buffer不会被填满自动停录(那个bug已经在Start里修了，这里做兜底)。
            int position;
            if (!TryEnsureMicrophone(out position))
            {
                if (m_ManualCaptureActive)
                    CancelManualCapture();
                if (m_LogTimings && Time.realtimeSinceStartup >= m_NextMicrophoneWarningTime)
                {
                    Debug.LogWarning("[RTSpeech] mic未就绪，等待设备/权限后重试");
                    m_NextMicrophoneWarningTime = Time.realtimeSinceStartup + 2f;
                }
                yield return new WaitForSecondsRealtime(0.25f);
                continue;
            }

            // Give the realtime lifecycle a short quiet boundary before VAD
            // starts promoting any stale/echo PCM to a new user turn.
            if (m_AwakeState && !m_IsRecording && !m_ManualCaptureActive &&
                Time.realtimeSinceStartup < m_AutoListeningBlockedUntil)
            {
                m_SmoothedRms = 0f;
                m_SilenceTimer = 0f;
                yield return null;
                continue;
            }

            float[] samples = new float[128]; // 选择合适的样本大小
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

            // PTT 捕获期间，整段音频由共享 ring buffer 累计；暂停 VAD 自动
            // 开始/收束，避免同一批 PCM 同时进入两条录音状态机。
            if (m_ManualCaptureActive)
            {
                CaptureRecordingAudio(position, false);
                if (m_ManualCaptureStartedAt >= 0f &&
                    Time.realtimeSinceStartup - m_ManualCaptureStartedAt >=
                    Mathf.Max(1f, m_ManualCaptureMaxSeconds))
                {
                    EndManualCapture();
                }
                yield return null;
                continue;
            }

            //barge-in分支：角色正在出声时，VAD的语义不是"开新一轮录音"而是"用户在打断"
            //单独处理可以避免和正常录音逻辑互相打架(m_LockState、m_IsRecording等)
            bool aiSpeaking = (m_ChatSample != null && m_ChatSample.IsAISpeaking);
            bool greetingPlaying = m_Greeting != null && m_Greeting.isPlaying;
            bool playbackProtected = greetingPlaying ||
                (m_ChatSample != null &&
                 m_ChatSample.IsAIPlaybackProtected(m_PostPlaybackEchoGuardSeconds));
            //完整最终录音独立于临时 partial 的回声保护持续累计。用户可能正是在
            //角色出声时插话；若像 WebSocket 一样跳过保护窗，会丢掉 barge-in 开头。
            if (m_IsRecording)
            {
                CaptureRecordingAudio(position, false);
                if (aiSpeaking || playbackProtected) MarkRecordingPlaybackOverlap();
            }
            if (!m_IsRecording && !aiSpeaking && !playbackProtected)
            {
                ObserveAmbientRms(m_SmoothedRms, false);
            }
            float startActivityThreshold = ResolveAdaptiveStartThreshold(
                m_SilenceThreshold,
                m_AmbientRmsFloor,
                m_AmbientNoiseCalibrated,
                m_QuietStartThreshold,
                m_AmbientStartMultiplier,
                m_MaxAdaptiveStartThreshold);
            float recordingActivityThreshold = EffectiveQuietThreshold;

            if (m_IsRecording && !aiSpeaking && !playbackProtected)
            {
                int oldRevision = m_TurnActivity.ActivityRevision;
                m_TurnActivity.ObserveLevel(m_SmoothedRms, Time.realtimeSinceStartup,
                    recordingActivityThreshold, EffectiveNoiseUpper);
                m_SilenceTimer = m_TurnActivity.QuietSeconds(Time.realtimeSinceStartup);
                if (m_TurnActivity.ActivityRevision != oldRevision)
                    InvalidateTentativeEou("effective-user-activity");
                ObserveListeningBoundaryEvidence(recordingActivityThreshold);
                PublishPreparedTurnBoundaryEvidence();
                // Analysis is private, cancellable preparation, not permission to close
                // the microphone or speak. Start it during boundary reasoning/ASR edits.
                if (RoleChoosesBoundary && m_EnableTentativeEou &&
                    !m_TentativeFired && !m_TentativePreviewInFlight &&
                    !string.IsNullOrWhiteSpace(m_LastMeaningfulStreamText) &&
                    CanPrefetchFinalAnalysis(m_SilenceTimer,
                        m_BoundaryAcoustics.DropSince < 0f ? 0f :
                            Time.realtimeSinceStartup - m_BoundaryAcoustics.DropSince,
                        m_TurnActivity.MelodyActive || m_TurnActivity.TextActivityActive,
                        Time.realtimeSinceStartup - m_TentativePreviewSentTime))
                    TryFireTentativeEou();
                MaybeRequestStalledTurnReview(
                    m_SmoothedRms,
                    recordingActivityThreshold);
                if (TryApplyPendingSemanticEou())
                {
                    yield return null;
                    continue;
                }
            }

            //用户录音已经成立后，旧回复才开始出声，说明发生了轮次竞态。此时用户拥有
            //绝对优先级：立即停掉旧回复，继续保留当前录音。正常“AI先说、用户后打断”
            //仍走下面的 AEC + 神经VAD Barge-In，不受此分支影响。
            if (aiSpeaking && m_IsRecording)
            {
                //本次录音若是靠哼唱豁免捞回来的，就**不允许它掐掉她的回复**。
                //实测环境噪音会走这条路把她打断：语音VAD正确地拒绝了(not is_speech)，
                //但 /vad 的哼唱豁免用快速探针把它捞了回来，于是录音成立 → 她刚要出声就被
                //Interrupt，日志里是 8 次 "[Interrupt] 角色被打断，已说: \"\""，几秒后正式
                //ASR 才判定"未检测到有效人声"——掐断发生在正确判定之前。
                //快速探针在这条路上没有可靠的判别维度：13 条噪音的基频**全部**报成 889Hz
                //(FFT 谱峰伪影，落在人声窗口正中)，而周期性 0.78~0.87 比真人哼唱还高，
                //`period >= 0.67` 这个门槛实际上是在挑选噪音。
                //所以改为拆开两件事：豁免仍可**启动录音**(真哼唱不会漏)，但**不打断**。
                //代价是你哼歌打断她时她会把当前这句说完——比被环境音掐掉自然得多。
                if (!m_RecordingInputConfirmed || m_RecordingRescuedByTonalOverride)
                {
                    if (m_LogTimings)
                        Debug.Log("[Turn] 本次录音尚未确认真人讲话或来自调性豁免，继续取证，不打断当前回复");
                    // Keep the same PCM capture, but allow a subsequently arriving
                    // real human to prove barge-in through the strict AEC/identity
                    // lane. Suspending partials must not suspend that lane too.
                    if (m_EnableNeuralVad && m_NeuralVadClient != null &&
                        m_SmoothedRms > startActivityThreshold)
                    {
                        if (m_BargeInTimer <= 0f) m_BargeInWindowStartTime = Time.realtimeSinceStartup;
                        m_BargeInTimer += Time.deltaTime;
                        if (m_BargeInTimer >= m_BargeInTriggerSeconds)
                            RequestNeuralVadProbe(position, true);
                    }
                    else m_BargeInTimer = Mathf.Max(0f, m_BargeInTimer - m_BargeInDecayRate * Time.deltaTime);
                    m_StreamLastSentPos = position;
                    yield return null;
                    continue;
                }
                if (m_LogTimings)
                    Debug.LogWarning("[Turn] 用户录音期间检测到旧AI开始发声，立即取消旧回复");
                if (m_TentativeFired || m_TentativePreviewInFlight)
                    InvalidateTentativeEou("stale-AI-during-user-turn");
                m_ChatSample.Interrupt();
                //这一帧可能同时包含刚起播的扬声器声音，partial 直接跳过；完整 ASR 仍保留
                //原始录音，且旧输出已经被立刻停止。
                m_StreamLastSentPos = position;
                yield return null;
                continue;
            }

            // 用户说话期间只发送 ring buffer 中尚未发送的新 samples。必须在AI播放保护
            //判定之后执行，避免把角色自己的外放声音送进流式识别。
            if (m_IsRecording && !playbackProtected)
            {
                PumpStreamingAudio(position, false);
            }

            // AudioSource playback is more authoritative than the logical flag.
            // If a stream starts/stops between coroutine frames, or if room echo
            // remains after Stop(), never let that window enter normal learn=True ASR.
            if (playbackProtected && !aiSpeaking)
            {
                if (m_TentativeFired || m_TentativePreviewInFlight)
                    InvalidateTentativeEou("playback-guard");
                if (m_NeuralVadProbeInFlight || m_PendingSpeechStartPos >= 0)
                    ResetNeuralVadGate("playback-guard");
                m_BargeInTimer = 0f;
                m_BargeInWindowStartTime = 0f;
                m_SmoothedRms = 0f;
                m_SilenceTimer = 0f;
                //保护窗内不把扬声器尾音积压到下一次 WebSocket 推送；这只影响临时
                //partial，最终整段 ASR 仍会从原始 ring buffer 校正。
                if (m_IsRecording)
                {
                    m_StreamLastSentPos = position;
                }
                yield return null;
                continue;
            }
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
                    Debug.Log($"[RMS] smoothed={m_SmoothedRms:F4} " +
                              $"start={startActivityThreshold:F4} eou={recordingActivityThreshold:F4} " +
                              $"floor={m_AmbientRmsFloor:F4} calibrated={m_AmbientNoiseCalibrated} " +
                              $"timer={m_BargeInTimer:F2}s");
                }

                if (m_SmoothedRms > startActivityThreshold)
                {
                    //timer从0刚抬起来的瞬间记录"用户开口"时刻，用于Interrupt时算累积说话时长
                    if (m_BargeInTimer <= 0f)
                    {
                        m_BargeInWindowStartTime = Time.realtimeSinceStartup;
                    }
                    m_BargeInTimer += Time.deltaTime;
                    if (m_BargeInTimer >= m_BargeInTriggerSeconds)
                    {
                        if (m_EnableNeuralVad && m_NeuralVadClient != null)
                        {
                            // 键盘/碰撞声即便持续越过 RMS，也必须通过人声确认后才能打断角色。
                            RequestNeuralVadProbe(position, true);
                        }
                        else
                        {
                            TriggerBargeIn(CalculateRecordingStartPos(position));
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

            // RMS 只负责发现“值得检查的声音”；真正开始录音由神经 VAD 确认。
            // 使用平滑 RMS 可以先滤掉单帧碰撞脉冲，减少无意义的 /vad 请求。
            float candidateActivityThreshold = ResolveCandidateActivityThreshold(startActivityThreshold);
            float activeRmsThreshold = m_IsRecording
                ? recordingActivityThreshold
                : candidateActivityThreshold;
            bool effectiveActivity = m_IsRecording
                ? m_TurnActivity.LevelActive || m_TurnActivity.MelodyActive || m_TurnActivity.TextActivityActive
                : m_SmoothedRms > activeRmsThreshold;
            if (effectiveActivity)
            {
                //While recording, the shared activity clock owns timing and invalidation.
                //One tiny RMS excursion is not proof that a user resumed speaking.

                //启动关键词唤醒监听(仅在配置了唤醒词模块时)
                if (m_VoiceAWake != null && !m_AwakeState && !m_ListeningState)
                {
                    StartVoiceListening();
                }
                //已唤醒，启动录制
                if (m_AwakeState&&!m_IsRecording)
                {
                    if (m_EnableNeuralVad && m_NeuralVadClient != null)
                    {
                        RequestNeuralVadProbe(position);
                    }
                    else
                    {
                        StartRecording();
                    }
                }

            }
            else
            {

                if (!m_IsRecording
                    && m_PendingSpeechStartPos >= 0
                    && m_LastNeuralVadCandidateTime >= 0f
                    && Time.realtimeSinceStartup - m_LastNeuralVadCandidateTime >= m_NeuralVadCandidateResetSeconds)
                {
                    ResetNeuralVadGate("candidate-ended");
                }

                if (!m_IsRecording && !m_LockState)
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
                bool melodicEouProtected = m_TurnActivity.MelodyActive;
                if (m_EnableTentativeEou
                    && m_AwakeState && m_IsRecording
                    && !melodicEouProtected
                    && !m_TurnActivity.PendingText
                    && m_SilenceTimer >= m_TentativeEouSilence
                    && !m_TentativeFired
                    && !m_TentativePreviewInFlight)
                {
                    TryFireTentativeEou();
                }

                //预测“像是说完了”后再留一个很短的恢复窗口。自然停顿中用户若继续说，
                //上面的 user-resumed 会让它失效；只有持续沉默才真正提交本轮。
                if (m_AwakeState && m_IsRecording && m_TentativeCompletePending
                    && m_SilenceTimer >= Mathf.Max(m_TentativeEouSilence, m_TentativeEouConfirmSilence))
                {
                    string confirmedText = m_TentativeCompleteText;
                    m_TentativeCompletePending = false;
                    m_TentativeCompleteText = "";
                    ConfirmEouFromPreview(confirmedText);
                }

                //歌唱使用独立停顿：不等普通对话的硬收束，也不被短句尾预测切碎。
                float activeEouSilence = (m_EnableSingingMode && (m_LikelySinging || m_MelodicEouProtectionActive))
                    ? m_SingingEouSilence
                    : m_RecordingTimeLimit;
                bool asrTailReady = !m_EnableStreamingRecognition ||
                    (HasFreshStreamingCoverage &&
                     m_TurnActivity.TailAvailable && m_TurnActivity.CoversQuietTail && !m_TurnActivity.PendingText);
                bool preparedOrdinaryClose = CanUsePreparedOrdinaryEndpoint(
                    m_RecordingInputConfirmed, m_TentativeUsedFullAsr && m_TentativeFullAsrResultReady &&
                        !m_TentativePreviewInFlight && !string.IsNullOrWhiteSpace(m_TentativeFullAsrText),
                    m_LikelySinging || m_MelodicEouProtectionActive || m_TurnActivity.MelodyActive,
                    HasFreshStreamingCoverage && m_TurnActivity.TailAvailable && m_TurnActivity.CoversQuietTail,
                    m_TurnActivity.LevelActive || m_TurnActivity.TextActivityActive || m_TurnActivity.PendingText,
                    m_TentativeFullAsrActivityRevision == m_TurnActivity.ActivityRevision &&
                        !HasSoundAdvanced(m_TentativeFullAsrSoundAt, m_TurnActivity.LatestSoundAt));
                if (preparedOrdinaryClose)
                    activeEouSilence = Mathf.Min(activeEouSilence,
                        Mathf.Max(m_TentativeEouSilence, m_TentativeEouConfirmSilence));
                bool pendingTextQuiet = HasFreshStreamingCoverage && !m_LikelySinging &&
                    !m_MelodicEouProtectionActive &&
                    m_TurnActivity.CanClosePendingTextOnQuietTail(Time.realtimeSinceStartup, activeEouSilence);
                if (m_IsRecording && m_SilenceTimer >= activeEouSilence && !asrTailReady &&
                    Time.realtimeSinceStartup >= m_NextCloseBlockTraceAt)
                {
                    m_NextCloseBlockTraceAt = Time.realtimeSinceStartup + 1f;
                    m_ChatSample?.TurnBoundaryTrace?.Sample("capture.close_evidence", new Newtonsoft.Json.Linq.JObject {
                        ["captureSerial"] = CurrentCaptureSerial, ["quietSeconds"] = m_SilenceTimer,
                        ["pendingText"] = m_TurnActivity.PendingText, ["freshCoverage"] = HasFreshStreamingCoverage,
                        ["tailAvailable"] = m_TurnActivity.TailAvailable, ["coversQuietTail"] = m_TurnActivity.CoversQuietTail,
                        ["melodicProtection"] = m_MelodicEouProtectionActive, ["pendingTextQuietClose"] = pendingTextQuiet,
                        ["scope"] = "capture gate only; final ASR remains required" });
                }
                bool degradedAsrTailReady = !m_EnableSingingMode ||
                    (!m_LikelySinging && !m_MelodicEouProtectionActive &&
                     HasDegradedQuietTailEvidence(Time.realtimeSinceStartup));
                //ASR outage is not silence by itself. A degraded close additionally
                //requires quiet-tail evidence and no active speech; the full audio
                //is still sent to final ASR.
                bool physicalQuietFallback = m_BoundaryAcoustics.QuietSince >= 0f &&
                    Time.realtimeSinceStartup - m_BoundaryAcoustics.QuietSince >= m_RecordingTimeLimit &&
                    (!m_TurnActivity.PendingText || !m_TurnActivity.AsrHealthy(Time.realtimeSinceStartup));
                bool shouldCloseOnDegradedTail = degradedAsrTailReady &&
                    (!asrTailReady || !m_TurnActivity.TailAvailable) &&
                    m_SilenceTimer >= Mathf.Min(activeEouSilence, m_DegradedAsrCloseSilence);
                if (m_AwakeState && m_IsRecording &&
                    ((m_SilenceTimer >= activeEouSilence && (asrTailReady || physicalQuietFallback || pendingTextQuiet)) ||
                     shouldCloseOnDegradedTail))
                {
                    //若同一份完整预测ASR已经覆盖了所有有效声音，硬EOU新增的只是
                    //静音尾巴。保留并晋升它，避免再对几十秒音频做一次完整分析。
                    bool reuseTentativeFullAsr = m_TentativeUsedFullAsr &&
                        (m_TentativePreviewInFlight || m_TentativeFullAsrResultReady);
                    if (!reuseTentativeFullAsr &&
                        (m_TentativeFired || m_TentativePreviewInFlight))
                    {
                        InvalidateTentativeEou("hard-timeout");
                    }
                    if (m_LogTimings)
                        Debug.Log($"[Listening/Endpoint] 有效输入静默={m_SilenceTimer:F2}s " +
                                  $"reference={recordingActivityThreshold:F4} noiseUpper={EffectiveNoiseUpper:F4} " +
                                  $"asrTailReady={asrTailReady} degradedTail={shouldCloseOnDegradedTail} " +
                                  $"recentMelody={m_TurnActivity.MelodyActive} role={m_LastStalledDecisionStatus} " +
                                  $"reuseFullAsr={reuseTentativeFullAsr}；收束采集，不强制接话");
                    if (m_LastStalledDecisionStatus == "continue" && m_ChatSample != null)
                        m_ChatSample.CommitStalledTurnDecisionNote("continue",
                            m_LastStalledDecisionConfidence, "uncertain", 0f, "uncertain");
                    TraceInputEvidence("endpoint", new Newtonsoft.Json.Linq.JObject {
                        ["quietSeconds"] = m_SilenceTimer, ["requiredQuietSeconds"] = activeEouSilence,
                        ["preparedOrdinary"] = preparedOrdinaryClose, ["freshCoverage"] = HasFreshStreamingCoverage,
                        ["singingProtected"] = m_LikelySinging || m_MelodicEouProtectionActive,
                        ["confirmedHuman"] = m_RecordingInputConfirmed, ["outputOverlap"] = m_RecordingOutputOverlap });
                    m_RecordingCloseReason = pendingTextQuiet ? "quiet-tail-await-final" : preparedOrdinaryClose ? "prepared-final-quiet" : asrTailReady ? "effective-quiet" :
                        (shouldCloseOnDegradedTail ? "coverage-degraded-quiet" : "physical-quiet-asr-unavailable");
                    StopRecording(reuseTentativeFullAsr);
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
                float environmentSpikeThreshold = Mathf.Max(
                    m_RmsSpikeThreshold,
                    m_AmbientNoiseCalibrated ? m_AmbientRmsFloor * 1.8f : 0f);
                if (isIdle && rms > environmentSpikeThreshold && m_ChatSample != null)
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
        if (m_GracefulDisableCoroutine != null)
        {
            StopCoroutine(m_GracefulDisableCoroutine);
            m_GracefulDisableCoroutine = null;
        }
        if (m_GracefulDisablePending && m_ChatSample != null)
            m_ChatSample.CancelGracefulAgentShutdown();
        m_GracefulDisablePending = false;
        //麦克风从整个 Play 生命周期开始就持续采样环境。实时模式只是“是否接收对话”
        //的开关，不能把已经学到的房间底噪清空；否则问候播放和回声保护结束后，
        //用户立即开口时永远凑不出新的空闲校准窗口。
        m_AwakeState = true;
        m_SilenceTimer = 0f;
        m_BargeInTimer = 0f;
        m_SmoothedRms = 0f;
        m_AutoListeningBlockedUntil =
            Time.realtimeSinceStartup + 0.75f;
        m_UnheardCapture.Clear();
        m_UnheardMic = null;
        EndStreamingRecognition();

        m_LastEnvSpikeNotifyTime = -1f;
        InvalidateTentativeEou("EnableRealtimeMode");
        ResetNeuralVadGate("EnableRealtimeMode");
        m_RecordingStartPos = -1;  //上一次会话残留的起点位置作废
        ResetRecordingCapture();

        //启动 Agent Loop —— 让角色拥有时间感、自主决定说话节奏
        if (m_ChatSample != null) m_ChatSample.StartAgentLoop();

        //保证mic在跑——刚启动应用时已经在跑了，但用户可能先关闭再开启，这里兜底
        TryEnsureMicrophone(out _);

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
    /// 显式退出实时对话状态。立即停止接受新的用户轮次，但当前正在录制或已经进入
    /// ASR/LLM/歌曲落盘的轮次会完整走完，最后一条成功/失败确认播完后才停止 Agent Loop。
    /// </summary>
    public void DisableRealtimeMode()
    {
        if (!m_AwakeState) return;
        m_AwakeState = false;
        m_GracefulDisablePending = true;
        m_UnheardCapture.Clear();
        m_UnheardMic = null;

        if (m_ChatSample != null) m_ChatSample.BeginGracefulAgentShutdown();

        //按钮可能恰好在用户说完最后一句时被按下。旧逻辑直接丢弃这段；现在把当前
        //ring-buffer快照正常送入ASR，之后 m_AwakeState=false 会阻止任何新录音。
        if (m_IsRecording)
        {
            m_RecordingCloseReason = "manual-disable";
            StopRecording();
        }
        else
        {
            EndStreamingRecognition();
            ResetRecordingCapture();
        }

        m_LockState = false;
        m_SilenceTimer = 0f;
        m_BargeInTimer = 0f;
        ResetNeuralVadGate("DisableRealtimeMode");

        InvalidateTentativeEou("DisableRealtimeMode");

        if (m_GracefulDisableCoroutine != null) StopCoroutine(m_GracefulDisableCoroutine);
        m_GracefulDisableCoroutine = StartCoroutine(CompleteRealtimeDisableWhenIdle());
        PrintLog("正在完成当前对话，完成后关闭实时模式...");
        UpdateRealtimeBtnLabel();
    }

    private IEnumerator CompleteRealtimeDisableWhenIdle()
    {
        float startedAt = Time.realtimeSinceStartup;
        while (m_GracefulDisablePending && m_ChatSample != null &&
            m_ChatSample.HasPendingConversationWork)
        {
            //网络层本身都有超时；这里再给一个宽松硬上限，避免异常provider永远不回调。
            if (Time.realtimeSinceStartup - startedAt > 180f)
            {
                Debug.LogWarning("[RTSpeech] 优雅关闭等待超过180秒，强制结束Agent Loop");
                break;
            }
            yield return null;
        }

        if (!m_GracefulDisablePending) yield break;
        if (m_ChatSample != null) m_ChatSample.StopAgentLoop();
        m_GracefulDisablePending = false;
        m_GracefulDisableCoroutine = null;
        PrintLog("Loss->实时对话已关闭（当前轮已完成）");
        UpdateRealtimeBtnLabel();
    }

    private void UpdateRealtimeBtnLabel()
    {
        if (m_RealtimeBtnLabel != null)
        {
            m_RealtimeBtnLabel.text = m_AwakeState
                ? m_LabelOn
                : m_GracefulDisablePending ? m_LabelClosing : m_LabelOff;
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
    /// RMS 候选触发后，把最近一小段 ring buffer 交给本地 FSMN-VAD。
    /// 请求期间麦克风仍持续录制；确认成功后使用第一次候选的起点，不会丢首音。
    /// </summary>
    private void RequestNeuralVadProbe(int currentPos, bool forBargeIn = false)
    {
        float now = Time.realtimeSinceStartup;
        m_LastNeuralVadCandidateTime = now;
        if (m_RecordedClip == null || m_NeuralVadClient == null) return;

        if (m_PendingSpeechStartPos < 0)
        {
            m_PendingSpeechStartPos = CalculateRecordingStartPos(currentPos);
            m_NeuralVadCandidateSince = now;
        }
        if (m_NeuralVadProbeInFlight || now < m_NextNeuralVadProbeTime) return;

        int totalSamples = m_RecordedClip.samples;
        int frequency = Mathf.Max(1, m_RecordedClip.frequency);
        float requestedProbeSeconds = forBargeIn
            ? Mathf.Max(m_NeuralVadProbeSeconds, m_BargeInSpeakerProbeSeconds)
            : m_NeuralVadProbeSeconds;
        int probeSamples = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Max(0.2f, requestedProbeSeconds) * frequency),
            128,
            Mathf.Max(128, totalSamples - 1));
        int probeStart = currentPos - probeSamples;
        while (probeStart < 0) probeStart += totalSamples;
        AudioClip probe = SnapshotFromBuffer(probeStart, currentPos);
        if (probe == null) return;

        AudioClip vadProbe = probe;
        ManagedEchoCanceller.Result aecResult = new ManagedEchoCanceller.Result();
        if (forBargeIn && m_EnableManagedAec && m_ChatSample != null &&
            m_ChatSample.EchoReferenceTap != null)
        {
            AudioClip cleaned = ManagedEchoCanceller.Cancel(
                probe,
                m_ChatSample.EchoReferenceTap,
                m_AecMaxDelayMs,
                m_AecMinCorrelation,
                m_AecStrength,
                out aecResult);
            if (cleaned != null) vadProbe = cleaned;
            if (m_LogTimings)
            {
                Debug.Log($"[AEC] applied={aecResult.Applied} corr={aecResult.Correlation:F3} " +
                          $"delay={aecResult.DelayMs:F1}ms gain={aecResult.Gain:F2} " +
                          $"rms={aecResult.InputRms:F4}->{aecResult.OutputRms:F4}");
            }
        }

        if (forBargeIn && aecResult.Applied && aecResult.InputRms > 0.00001f)
        {
            float localResidualRatio = aecResult.OutputRms / aecResult.InputRms;
            bool echoDominated = aecResult.Correlation >= m_LocalEchoVetoCorrelation &&
                                 localResidualRatio <= m_LocalEchoVetoResidualRatio;
            if (echoDominated)
            {
                TraceInputProbeEvidence(true, false, null, aecResult);
                if (vadProbe != probe) Destroy(vadProbe);
                Destroy(probe);
                m_BargeInTimer = 0f;
                m_BargeInWindowStartTime = 0f;
                ResetNeuralVadGate("local-echo-veto");
                m_NextNeuralVadProbeTime = now + Mathf.Max(0.1f, m_BargeInVadRetrySeconds);
                if (m_LogTimings)
                {
                    Debug.Log($"[Barge-in] local echo veto corr={aecResult.Correlation:F3} " +
                              $"residual={localResidualRatio:F2}");
                }
                return;
            }
        }

        m_NeuralVadProbeInFlight = true;
        int sequence = ++m_NeuralVadSequence;
        //回调里要用，但 probe 在下面就被 Destroy 了，先捕获长度
        float probeSecondsForLog = probe.length;
        if (m_LogTimings)
            Debug.Log($"[VAD] 候选音量通过，探测最近 {probeSecondsForLog:F2}s 音频 (seq={sequence})");

        byte[] rawProbeBytes = WavUtility.FromAudioClip(probe);
        byte[] vadProbeBytes = vadProbe == probe
            ? rawProbeBytes
            : WavUtility.FromAudioClip(vadProbe);
        if (vadProbe != probe) Destroy(vadProbe);
        Destroy(probe);

        int probeCaptureSerial = CurrentCaptureSerial;
        bool probeStartedInRecording = m_IsRecording;
        bool handlingAecResidual = false;
        string rawSpeakerIdForResidual = string.Empty;
        float rawSpeakerScoreForResidual = 0f;
        float aecResidualRatioForDecision = 0f;
        System.Action<SenseVoiceSpeechToText.VoiceActivityResult> handleVadResult = vadResult =>
        {
            if (sequence != m_NeuralVadSequence) return;
            if (!IsVadProbeCaptureCurrent(probeCaptureSerial, probeStartedInRecording))
            {
                ResetNeuralVadGate("probe-capture-changed");
                return;
            }

            m_NeuralVadProbeInFlight = false;
            float retrySeconds = forBargeIn ? m_BargeInVadRetrySeconds : m_NeuralVadRetrySeconds;
            m_NextNeuralVadProbeTime = Time.realtimeSinceStartup + Mathf.Max(0.05f, retrySeconds);
            bool isSpeech = vadResult != null && vadResult.IsSpeech;
            //探测长度是关键变量：只给 FSMN m_NeuralVadProbeSeconds(默认 0.5s)，还要求其中
            //至少 min_speech_ms 是语音，而探测发生在起音瞬间。服务端 [VAD] 那行有对应细节。
            if (m_LogTimings)
                Debug.Log($"[VAD] 探测 {probeSecondsForLog:F2}s → speech={isSpeech} " +
                          $"is_singing={(vadResult != null && vadResult.IsSinging)} " +
                          $"speech_ms={(vadResult != null ? vadResult.SpeechMs : 0)} " +
                          $"forBargeIn={forBargeIn} (seq={sequence})");
            TraceInputProbeEvidence(forBargeIn, isSpeech, vadResult, aecResult);
            if (!isSpeech)
            {
                // A short onset probe can finish before the first syllable is
                // represented strongly enough for FSMN-VAD. If RMS has stayed
                // active across repeated probes, let the complete ASR decide
                // instead of silently losing the realtime turn. This fallback
                // is only for normal capture; barge-in keeps its stricter gate.
                if (!forBargeIn && m_AwakeState && !m_IsRecording &&
                    m_PendingSpeechStartPos >= 0 &&
                    m_NeuralVadCandidateSince >= 0f &&
                    Time.realtimeSinceStartup - m_NeuralVadCandidateSince >=
                        Mathf.Max(0.25f, m_NeuralVadFallbackSeconds) &&
                    m_SmoothedRms > ResolveCandidateActivityThreshold(
                        ResolveAdaptiveStartThreshold(
                            m_SilenceThreshold,
                            m_AmbientRmsFloor,
                            m_AmbientNoiseCalibrated,
                            m_QuietStartThreshold,
                            m_AmbientStartMultiplier,
                            m_MaxAdaptiveStartThreshold)))
                {
                    if (TryDeferRmsContinuation(vadResult, now, probeSecondsForLog, forBargeIn))
                        return;
                    int fallbackStart = m_PendingSpeechStartPos;
                    if (m_LogTimings)
                        Debug.Log($"[VAD] 短探针连续否定但 RMS 持续；转完整 ASR 兜底录音 (wait={Time.realtimeSinceStartup - m_NeuralVadCandidateSince:F2}s)");
                    ResetNeuralVadGate("rms-fallback-after-vad-reject");
                    StartRecording(fallbackStart, true,
                        vadResult != null && vadResult.IsSinging,
                        vadResult != null ? vadResult.SingingProbability : 0f, false);
                    return;
                }
                if (m_LogTimings) Debug.Log($"[VAD] 拒绝非人声候选 (seq={sequence})");
                if (forBargeIn) m_BargeInTimer = 0f;
                else ObserveAmbientRms(m_SmoothedRms, true);
                return;
            }

            bool aiSpeaking = m_ChatSample != null && m_ChatSample.IsAISpeaking;
            if (forBargeIn)
            {
                if (vadResult.SpeakerStatus == "error" || vadResult.SpeakerVoiceprintStatus == "error")
                {
                    m_BargeInTimer = 0f;
                    m_BargeInWindowStartTime = 0f;
                    ResetNeuralVadGate("speaker-verification-unavailable");
                    return;
                }
                if (!m_AwakeState || !aiSpeaking ||
                    (m_IsRecording && m_RecordingInputConfirmed && !m_RecordingRescuedByTonalOverride))
                {
                    ResetNeuralVadGate("barge-in-state-changed");
                    return;
                }

                //打断必须由语音VAD本身认可。IsSinging=true 意味着语音VAD其实拒绝了、
                //是哼唱豁免把它捞回来的——那种证据强度可以开录音，但不足以掐断她的话。
                //(依据同上：快速探针对噪音的基频/周期性都不可用)
                if (vadResult.IsSinging)
                {
                    if (m_LogTimings)
                        Debug.Log("[Barge-in] 仅靠哼唱豁免通过，不作为打断依据");
                    m_BargeInTimer = 0f;
                    m_BargeInWindowStartTime = 0f;
                    ResetNeuralVadGate("tonal-override-only");
                    return;
                }

                // AEC residuals may retain some AI similarity. Only the best
                // identity being AI_SELF is a hard veto; an independent self
                // score must not override a stronger confirmed-human match.
                bool isSelf = vadResult.SpeakerId == "ai_self" || vadResult.SpeakerKind == "ai";
                if (isSelf)
                {
                    if (m_LogTimings)
                        Debug.Log($"[Barge-in] 抑制 AI_SELF 外放回声 score={vadResult.SpeakerConfidence:F3}");
                    m_BargeInTimer = 0f;
                    m_BargeInWindowStartTime = 0f;
                    ResetNeuralVadGate("AI-self-echo");
                    return;
                }

                bool knownHuman = vadResult.SpeakerKind == "owner" || vadResult.SpeakerKind == "guest";
                bool confirmedIdentity = vadResult.SpeakerStatus == "confirmed";
                float requiredConfidence = handlingAecResidual
                    ? Mathf.Max(m_MinBargeInSpeakerConfidence, m_MinAecResidualSpeakerConfidence)
                    : m_MinBargeInSpeakerConfidence;
                bool confidentHuman = knownHuman && confirmedIdentity
                    && vadResult.SpeakerConfidence >= requiredConfidence;
                bool sameIdentityAcrossRawAndResidual = !handlingAecResidual ||
                    string.IsNullOrEmpty(rawSpeakerIdForResidual) ||
                    vadResult.SpeakerId == rawSpeakerIdForResidual;
                confidentHuman = confidentHuman && sameIdentityAcrossRawAndResidual;
                if (!confidentHuman && !m_AllowUnknownBargeIn)
                {
                    if (m_LogTimings)
                        Debug.Log($"[Barge-in] 身份不确定，继续等待 speaker={vadResult.SpeakerId} " +
                                  $"kind={vadResult.SpeakerKind} score={vadResult.SpeakerConfidence:F3}");
                    m_BargeInTimer = 0f;
                    m_BargeInWindowStartTime = 0f;
                    ResetNeuralVadGate("unknown-barge-in");
                    return;
                }

                Debug.Log($"[Barge-in] confirmed near-end human speaker={vadResult.SpeakerId} " +
                          $"residualScore={vadResult.SpeakerConfidence:F3} " +
                          $"rawSpeaker={rawSpeakerIdForResidual} rawScore={rawSpeakerScoreForResidual:F3} " +
                          $"aecRatio={aecResidualRatioForDecision:F2}");
                TriggerBargeIn(m_PendingSpeechStartPos);
                return;
            }

            if (!m_AwakeState || m_IsRecording || aiSpeaking)
            {
                ResetNeuralVadGate("state-changed");
                return;
            }

            int confirmedStart = m_PendingSpeechStartPos;
            //空闲期会接纳所有未确认声音，用低分位数解决持续外放环境无法启动校准的问题。
            //这里一旦确认真人开口，撤回最近探测窗口，避免把起音和短句混进环境基线。
            DiscardRecentAmbientSamples(m_AmbientSpeechRollbackSeconds);
            if (m_LogTimings) Debug.Log($"[VAD] 确认人声，开始正式录音 (seq={sequence})");
            StartRecording(
                confirmedStart,
                true,
                vadResult.IsSinging,
                vadResult.SingingProbability);
        };

        if (!forBargeIn)
        {
            m_NeuralVadClient.CheckVoiceActivityDetailed(rawProbeBytes, false, handleVadResult);
            return;
        }

        // Stage 1 always checks the untouched microphone signal. AEC residuals
        // must never hide a strong AI_SELF match.
        m_NeuralVadClient.CheckVoiceActivityDetailed(rawProbeBytes, true, rawResult =>
        {
            if (sequence != m_NeuralVadSequence) return;
            if (!IsVadProbeCaptureCurrent(probeCaptureSerial, probeStartedInRecording))
            {
                ResetNeuralVadGate("probe-capture-changed");
                return;
            }

            TraceInputProbeEvidence(true, rawResult != null && rawResult.IsSpeech, rawResult, aecResult);
            bool rawConfirmedHuman = rawResult != null && rawResult.IsSpeech &&
                (rawResult.SpeakerKind == "owner" || rawResult.SpeakerKind == "guest") &&
                rawResult.SpeakerStatus == "confirmed" &&
                rawResult.SpeakerConfidence >= m_MinBargeInSpeakerConfidence;

            bool rawContainsSelf = rawResult != null && rawResult.IsSpeech &&
                (rawResult.SpeakerId == "ai_self" || rawResult.SpeakerKind == "ai" ||
                 rawResult.SelfConfidence >= m_MinRawSelfConfidence);

            // Raw microphone audio necessarily contains playback while the AI is
            // speaking, so an AI_SELF hit is only risk evidence, never an early
            // hard veto. Prefer the AEC residual. If AEC is unavailable, retain a
            // deliberately strict dominant-human fallback instead of disabling
            // barge-in completely.
            if (vadProbeBytes == rawProbeBytes || !aecResult.Applied)
            {
                bool strongRawHuman = rawConfirmedHuman &&
                    rawResult.SpeakerConfidence >= m_MinNoAecBargeInSpeakerConfidence &&
                    rawResult.SpeakerConfidence >=
                        rawResult.SelfConfidence + m_MinNoAecHumanOverSelfMargin;
                if (strongRawHuman)
                {
                    if (m_LogTimings)
                        Debug.Log($"[Barge-in] AEC unavailable; accepting dominant human " +
                                  $"speaker={rawResult.SpeakerId} score={rawResult.SpeakerConfidence:F3} " +
                                  $"self={rawResult.SelfConfidence:F3}");
                    handleVadResult(rawResult);
                    return;
                }

                if (m_LogTimings)
                    Debug.Log($"[Barge-in] AEC unavailable; suppressing ambiguous raw audio " +
                              $"speaker={rawResult?.SpeakerId} score={rawResult?.SpeakerConfidence:F3} " +
                              $"self={rawResult?.SelfConfidence:F3} containsSelf={rawContainsSelf}");
                m_BargeInTimer = 0f;
                m_BargeInWindowStartTime = 0f;
                ResetNeuralVadGate("barge-in-no-aec-separation");
                return;
            }

            float residualRatio = aecResult.InputRms > 0.00001f
                ? aecResult.OutputRms / aecResult.InputRms
                : 0f;
            aecResidualRatioForDecision = residualRatio;
            if (residualRatio < m_MinAecHumanResidualRatio)
            {
                if (m_LogTimings)
                    Debug.Log($"[Barge-in] AEC 后残余能量过低，按纯回声抑制 ratio={residualRatio:F2}");
                m_BargeInTimer = 0f;
                m_BargeInWindowStartTime = 0f;
                ResetNeuralVadGate("AEC-echo-dominated");
                return;
            }

            // Stage 2 may confirm an established human, but uses a higher
            // threshold and the server-side identity lookup is read-only. If raw
            // audio already named a human, the residual must name the same person.
            if (rawConfirmedHuman)
            {
                rawSpeakerIdForResidual = rawResult.SpeakerId;
                rawSpeakerScoreForResidual = rawResult.SpeakerConfidence;
            }
            handlingAecResidual = true;
            m_NeuralVadClient.CheckVoiceActivityDetailed(vadProbeBytes, true, handleVadResult);
        });
    }

    private void TriggerBargeIn(int confirmedStartPos)
    {
        if (m_ChatSample == null || !m_ChatSample.IsAISpeaking) return;
        if (m_LogTimings)
        {
            float spoken = m_BargeInWindowStartTime > 0f
                ? Time.realtimeSinceStartup - m_BargeInWindowStartTime
                : m_BargeInTriggerSeconds;
            Debug.Log($"[Timing] ★ Barge-in 人声确认：连续说话 {spoken:F2}s");
        }
        PrintLog("Barge-in->用户打断角色发言");
        if (m_IsRecording)
        {
            // This capture already contains the onset. Reopening it would lose
            // evidence and could bind a stale probe to a different input owner.
            m_CurrentRecordingAllowsSpeakerLearning = false;
            m_RecordingRescuedByTonalOverride = false;
            bool alreadyConfirmed = m_RecordingInputConfirmed;
            ConfirmRecordingInput("aec-identity-barge-in");
            if (alreadyConfirmed) m_ChatSample.Interrupt();
            m_BargeInTimer = 0f;
            m_BargeInWindowStartTime = 0f;
            ResetNeuralVadGate("existing-capture-barge-in-confirmed");
            return;
        }
        m_ChatSample.Interrupt();
        m_BargeInTimer = 0f;
        m_BargeInWindowStartTime = 0f;
        if (m_AwakeState && !m_IsRecording)
            StartRecording(confirmedStartPos, false);
    }

    private int CalculateRecordingStartPos(int currentPos)
    {
        return CalculateRecordingStartPos(currentPos, m_RecordingPreRollSeconds);
    }

    private int CalculateRecordingStartPos(int currentPos, float preRollSeconds)
    {
        int totalSamples = m_RecordedClip != null ? m_RecordedClip.samples : 16000 * 30;
        int frequency = m_RecordedClip != null ? Mathf.Max(1, m_RecordedClip.frequency) : 16000;
        int preRollSamples = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Max(0f, preRollSeconds) * frequency),
            0,
            Mathf.Max(0, totalSamples - 1));
        int startPos = currentPos - preRollSamples;
        while (startPos < 0) startPos += totalSamples;
        return startPos;
    }

    private void ResetNeuralVadGate(string reason)
    {
        if (m_LogTimings && (m_NeuralVadProbeInFlight || m_PendingSpeechStartPos >= 0))
            Debug.Log($"[VAD] 重置门控: {reason}");
        m_NeuralVadSequence++;
        m_NeuralVadProbeInFlight = false;
        m_NextNeuralVadProbeTime = 0f;
        m_LastNeuralVadCandidateTime = -1f;
        m_NeuralVadCandidateSince = -1f;
        m_PendingSpeechStartPos = -1;
    }

    private bool TryDeferRmsContinuation(SenseVoiceSpeechToText.VoiceActivityResult vad,
        float probeEndedAt, float probeSeconds, bool forBargeIn)
    {
        var provider = m_NeuralVadClient;
        var evidence = new RecordingContinuationGate.Evidence {
            CurrentCapture = CurrentCaptureSerial, RetainedCapture = m_UnheardCaptureSessionSerial,
            FinalCapture = provider != null ? provider.LastFinalCaptureSerial : 0,
            FinalSource = provider != null ? provider.LastFinalTranscriptSource : "",
            FinalText = provider != null ? provider.LastFinalRawText : "",
            HasRetainedAudio = m_UnheardCapture.Count > 0, SameMicrophone = m_UnheardMic == m_RecordedClip,
            Replied = m_ChatSample != null && (m_ChatSample.IsVoiceOutputPlaying ||
                m_ChatSample.VoiceOutputRevision != m_UnheardVoiceRevision),
            RejectedSelfSpeech = provider != null && provider.IsCaptureRejectedSelfSpeech(m_UnheardCaptureSessionSerial),
            BargeIn = forBargeIn, VadAvailable = vad != null && vad.Available,
            Speech = vad != null && vad.IsSpeech, Tonal = vad != null && vad.IsSinging,
            SpeechMs = vad != null ? vad.SpeechMs : -1, Now = Time.realtimeSinceStartup,
            ClosedAt = m_UnheardCaptureClosedAt, ProbeEndedAt = probeEndedAt, ProbeSeconds = probeSeconds
        };
        if (!RecordingContinuationGate.ShouldDeferRmsFallback(evidence)) return false;
        if (m_LastDeferredRmsContinuationCapture != m_UnheardCaptureSessionSerial)
        {
            m_LastDeferredRmsContinuationCapture = m_UnheardCaptureSessionSerial;
            TraceInputEvidence("continuation_deferred", new Newtonsoft.Json.Linq.JObject {
                ["retainedCaptureSerial"] = m_UnheardCaptureSessionSerial,
                ["firstCaptureSerial"] = m_UnheardFirstCaptureSerial,
                ["firstCaptureRetainedUtc"] = m_UnheardFirstCloseUtc,
                ["previousCaptureRetainedUtc"] = m_UnheardCloseUtc,
                ["probeAgeSeconds"] = evidence.Now - probeEndedAt,
                ["probeSeconds"] = probeSeconds,
                ["retainedAgeSeconds"] = evidence.Now - evidence.ClosedAt,
                ["probeStartAfterCloseSeconds"] = probeEndedAt - probeSeconds - evidence.ClosedAt,
                ["reason"] = "fresh-negative-vad-rms-only",
                ["scope"] = "keep pending reply and onset cursor; no new capture or transcript deduplication"
            });
        }
        // Keep m_PendingSpeechStartPos and candidate time: a later positive VAD
        // uses the original onset/pre-roll. No capture, ASR, or reply is cancelled.
        return true;
    }

    private void ObserveAmbientRms(float observedRms, bool vadConfirmedNonSpeech)
    {
        float now = Time.realtimeSinceStartup;
        //A negative short speech-VAD probe does not prove that this is environment noise:
        //singing and room echo can also be rejected. Do not overweight those callbacks.
        if (m_IsRecording || now < m_NextAmbientSampleTime || now < m_AmbientHandoffHoldUntil ||
            (m_ChatSample != null && (m_ChatSample.IsAISpeaking ||
                m_ChatSample.IsAIPlaybackProtected(m_PostPlaybackEchoGuardSeconds)))) return;

        m_NextAmbientSampleTime = now + Mathf.Max(0.05f, m_AmbientSampleIntervalSeconds);
        float clamped = Mathf.Clamp(
            observedRms,
            0f,
            Mathf.Max(m_MaxAdaptiveStartThreshold, m_SilenceThreshold * 4f));
        m_AmbientRmsSamples.Add(clamped);
        m_AmbientRmsSampleTimes.Add(now);
        float oldestAllowed = now - Mathf.Max(4f, m_AmbientWindowSeconds);
        while (m_AmbientRmsSampleTimes.Count > 0 &&
               m_AmbientRmsSampleTimes[0] < oldestAllowed)
        {
            m_AmbientRmsSampleTimes.RemoveAt(0);
            m_AmbientRmsSamples.RemoveAt(0);
        }

        //Promote samples only after the onset rollback window has passed. A user who
        //starts speaking now must not already have changed their own detection threshold.
        var trusted = SelectMatureAmbientSamples(m_AmbientRmsSamples, m_AmbientRmsSampleTimes,
            now, m_AmbientSpeechRollbackSeconds);
        if (trusted.Count == 0) return;

        float target = EstimateAmbientFloor(
            trusted,
            m_AmbientFloorQuantile);
        float upperTarget = EstimateAmbientFloor(trusted, 0.90f);
        bool wasCalibrated = m_AmbientNoiseCalibrated;
        m_AmbientRmsUpper = !wasCalibrated ? upperTarget :
            Mathf.Lerp(m_AmbientRmsUpper, upperTarget, 1f - Mathf.Exp(-0.4f * Mathf.Max(0.05f, m_AmbientSampleIntervalSeconds)));
        if (!wasCalibrated || m_AmbientRmsFloor <= 0f)
        {
            //The serialized 0.01 is only the safe boot threshold, not a real
            //measurement.  Do not spend several more seconds slewing away from it
            //after the first robust window is already available.
            m_AmbientRmsFloor = Mathf.Max(0f, target);
        }
        else
        {
            float rate = target > m_AmbientRmsFloor
                ? Mathf.Max(0.01f, m_AmbientFloorRiseRate)
                : Mathf.Max(0.01f, m_AmbientFloorFallRate);
            float elapsed = Mathf.Max(0.05f, m_AmbientSampleIntervalSeconds);
            float blend = 1f - Mathf.Exp(-rate * elapsed);
            m_AmbientRmsFloor = Mathf.Lerp(m_AmbientRmsFloor, target, blend);
        }
        m_AmbientNoiseCalibrated = true;
        bool changedEnough = m_LastLoggedAmbientFloor < 0f ||
            Mathf.Abs(m_AmbientRmsFloor - m_LastLoggedAmbientFloor) >=
                Mathf.Max(0.0005f, m_LastLoggedAmbientFloor * 0.20f);
        if (m_LogTimings && (!wasCalibrated ||
            (changedEnough && now - m_LastAmbientCalibrationLogTime >= 5f)))
        {
            float start = ResolveAdaptiveStartThreshold(
                m_SilenceThreshold, m_AmbientRmsFloor, true,
                m_QuietStartThreshold, m_AmbientStartMultiplier,
                m_MaxAdaptiveStartThreshold);
            float eou = EffectiveQuietThreshold;
            Debug.Log($"[RMS/Adaptive] 底噪={m_AmbientRmsFloor:F4} " +
                      $"噪声上沿={m_AmbientRmsUpper:F4} " +
                      $"样本={trusted.Count}/{m_AmbientRmsSamples.Count} start={start:F4} eou={eou:F4} " +
                      $"来源=延迟确认的空闲分位 VAD负例={vadConfirmedNonSpeech}");
            m_LastAmbientCalibrationLogTime = now;
            m_LastLoggedAmbientFloor = m_AmbientRmsFloor;
        }
    }

    private void DiscardRecentAmbientSamples(float rollbackSeconds)
    {
        if (m_AmbientRmsSampleTimes.Count == 0) return;
        float cutoff = Time.realtimeSinceStartup - Mathf.Max(0.5f, rollbackSeconds);
        int removed = 0;
        for (int i = m_AmbientRmsSampleTimes.Count - 1; i >= 0; i--)
        {
            if (m_AmbientRmsSampleTimes[i] < cutoff) break;
            m_AmbientRmsSampleTimes.RemoveAt(i);
            m_AmbientRmsSamples.RemoveAt(i);
            removed++;
        }

        //Keep the last calibrated floor. Removing provisional samples must not revert
        //to boot 0.01 or jump directly to a tiny, possibly contaminated new window.
        if (m_LogTimings && removed > 0)
            Debug.Log($"[RMS/Adaptive] VAD确认真人，回滚最近环境样本={removed}，" +
                      $"剩余={m_AmbientRmsSamples.Count} calibrated={m_AmbientNoiseCalibrated}");
    }

    private void ResetAmbientCalibration()
    {
        m_AmbientRmsSamples.Clear();
        m_AmbientRmsSampleTimes.Clear();
        m_NextAmbientSampleTime = 0f;
        m_AmbientRmsFloor = Mathf.Max(0f, m_SilenceThreshold);
        m_AmbientRmsUpper = Mathf.Max(0.001f, m_SilenceThreshold * 0.5f);
        m_AmbientNoiseCalibrated = false;
        m_LastAmbientCalibrationLogTime = -999f;
        m_LastLoggedAmbientFloor = -1f;
        m_AmbientHandoffHoldUntil = 0f;
    }

    private static List<float> SelectMatureAmbientSamples(List<float> values, List<float> times,
        float now, float rollbackSeconds)
    {
        var result = new List<float>();
        float cutoff = now - Mathf.Max(2f, rollbackSeconds);
        float first = -1f, last = -1f;
        for (int i = 0; i < values.Count && i < times.Count; i++)
        {
            if (times[i] > cutoff) continue;
            if (first < 0f) first = times[i];
            last = times[i];
            result.Add(values[i]);
        }
        if (result.Count < 8 || last - first < 3f) result.Clear();
        return result;
    }

    private void HoldAmbientLearningAtHandoff()
    {
        m_AmbientHandoffHoldUntil = Time.realtimeSinceStartup +
            Mathf.Max(2f, m_AmbientSpeechRollbackSeconds);
        if (m_LogTimings)
            Debug.Log($"[RMS/Adaptive] 轮次交接暂停底噪入库；保留 floor={m_AmbientRmsFloor:F4} " +
                $"calibrated={m_AmbientNoiseCalibrated}，新样本仍需经过成熟窗口");
    }

    private static float EstimateAmbientFloor(IList<float> samples, float quantile)
    {
        if (samples == null || samples.Count == 0) return 0f;
        var sorted = new List<float>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
            sorted.Add(Mathf.Max(0f, samples[i]));
        sorted.Sort();
        float position = Mathf.Clamp01(quantile) * (sorted.Count - 1);
        int lower = Mathf.FloorToInt(position);
        int upper = Mathf.Min(sorted.Count - 1, lower + 1);
        return Mathf.Lerp(sorted[lower], sorted[upper], position - lower);
    }

    private static float ResolveAdaptiveStartThreshold(
        float configuredThreshold,
        float ambientFloor,
        bool calibrated,
        float quietMinimum,
        float ambientMultiplier,
        float maximum)
    {
        float configured = Mathf.Max(0f, configuredThreshold);
        if (!calibrated) return configured;
        float upper = Mathf.Max(Mathf.Max(quietMinimum, configured), maximum);
        return Mathf.Clamp(
            Mathf.Max(Mathf.Max(0f, quietMinimum),
                      Mathf.Max(0f, ambientFloor) * Mathf.Max(1f, ambientMultiplier)),
            0f,
            upper);
    }

    private float ResolveCandidateActivityThreshold(float adaptiveStartThreshold)
    {
        if (!m_EnableNeuralVad || m_NeuralVadClient == null)
            return adaptiveStartThreshold;

        // Neural VAD is the expensive/noisy gate; let RMS nominate a quieter
        // candidate so low-level speech can reach it. The full ASR/final turn
        // still decides whether the captured clip contains usable speech.
        return Mathf.Min(
            adaptiveStartThreshold,
            Mathf.Max(m_QuietStartThreshold, m_SilenceThreshold * 1.25f));
    }

    private static float ResolveAdaptiveEouThreshold(
        float configuredThreshold,
        float ambientFloor,
        bool calibrated,
        float quietMinimum,
        float ambientMultiplier,
        float startThreshold)
    {
        if (!calibrated) return Mathf.Max(0f, configuredThreshold);
        return Mathf.Clamp(
            Mathf.Max(Mathf.Max(0f, quietMinimum),
                      Mathf.Max(0f, ambientFloor) * Mathf.Max(1f, ambientMultiplier)),
            0f,
            Mathf.Max(0f, startThreshold));
    }

    //Kept as a compatibility helper for the existing regression and serialized
    //prototype.  New runtime code uses the calibrated start/end pair above.
    private static float ResolveRecordingActivityThreshold(
        float configuredThreshold,
        float ambientFloor,
        float ambientMultiplier)
    {
        return Mathf.Max(
            Mathf.Max(0f, configuredThreshold),
            Mathf.Max(0f, ambientFloor) * Mathf.Max(1f, ambientMultiplier));
    }

    private void HandleStableUserSingingObserved(
        float singingProbability,
        float pitchStability,
        int audioMs)
    {
        if (!m_IsRecording || !m_EnableSingingMode) return;
        bool newlyDetected = !m_LikelySinging;
        m_LikelySinging = true;
        m_MelodicEouProtectionActive = true;
        m_MelodicEouEvidenceFrames = Mathf.Max(3, m_MelodicEouEvidenceFrames);
        m_MelodicEouLowFrames = 0;
        m_CurrentSingingProbability = Mathf.Max(
            m_CurrentSingingProbability,
            Mathf.Clamp01(singingProbability));
        if ((!RoleChoosesBoundary || m_TurnActivity.MelodyActive) &&
            (m_TentativeFired || m_TentativePreviewInFlight || m_TentativeCompletePending))
            InvalidateTentativeEou("stable-singing-evidence");
        if (newlyDetected && m_LogTimings)
        {
            Debug.Log($"[Singing] 稳定歌唱证据回传录音状态机 " +
                      $"p={singingProbability:F2} pitch={pitchStability:F2} " +
                      $"audio={audioMs}ms；切换停唱判定");
        }
    }

    private void HandleStableUserSpeechObserved(float confidence, int audioMs)
    {
        if (!m_IsRecording || !m_LikelySinging) return;
        m_LikelySinging = false;
        m_RecordingRescuedByTonalOverride = false;
        m_CurrentSingingProbability = 0f;
        //不直接提交 EOU，也不改静默计时：这里只撤掉歌唱专用锁，随后仍由正常
        //RMS/VAD + tentative-EOU 判断用户是否真的说完。
        if (m_LogTimings)
        {
            Debug.Log($"[Singing] 角色语义复核判定普通说话 " +
                      $"confidence={confidence:F2} audio={audioMs}ms；" +
                      "解除歌唱停顿模式并恢复普通 EOU");
        }
    }

    /// <summary>
    /// 开始监听说话声音。forcedStartPos 用于神经 VAD 异步确认后恢复首个候选起点。
    /// </summary>
    private bool m_RecordingInputConfirmed;
    private int m_ConfirmationLastAudioMs = -1;
    private int m_ConfirmationMelodyAudioMs = -1;
    private int m_ConfirmationMelodyFrames;
    private int m_InheritedFinalCapture, m_InheritedContinuationCapture;
    private string m_InheritedFinalText;
    private AudioClip m_InheritedFinalMicrophone;
    private float m_ContinuationJoinSeconds;

    // A repeated transcript alone is never a duplicate-input test. This applies
    // only to a known copied PCM prefix with no independently confirmed new voice.
    public bool IsUnconfirmedInheritedFinal(int acceptedCapture, int finalCapture, string rawText) =>
        !m_IsRecording && !m_RecordingInputConfirmed && acceptedCapture > 0 &&
        acceptedCapture == m_InheritedFinalCapture && finalCapture == m_InheritedContinuationCapture &&
        finalCapture == CurrentCaptureSerial && m_InheritedFinalMicrophone != null && m_RecordedClip != null &&
        m_InheritedFinalMicrophone == m_RecordedClip &&
        !string.IsNullOrWhiteSpace(m_InheritedFinalText) &&
        string.Equals(rawText, m_InheritedFinalText, System.StringComparison.Ordinal);

    private bool m_RecordingOutputOverlap;
    private ConversationTrace m_InputEvidenceTrace;
    private int m_InputEvidenceCount;
    private float m_NextInputProbeTraceAt;

    private bool IsVadProbeCaptureCurrent(int serial, bool startedInRecording) =>
        CurrentCaptureSerial == serial && m_IsRecording == startedInRecording;

    private static bool CanUsePreparedOrdinaryEndpoint(bool confirmedHuman, bool fullFinalReady,
        bool singingProtected, bool freshCoveredQuiet, bool activeOrPending, bool sameActivityRevision) =>
        confirmedHuman && fullFinalReady && !singingProtected && freshCoveredQuiet &&
        !activeOrPending && sameActivityRevision;

    private void MarkRecordingPlaybackOverlap()
    {
        // Mixed microphone/playback is usable evidence, but not a clean voiceprint
        // enrolment sample. Do not discard quiet near-end speech or singing PCM.
        m_CurrentRecordingAllowsSpeakerLearning = false;
        if (m_RecordingOutputOverlap) return;
        m_RecordingOutputOverlap = true;
        TraceInputEvidence("playback_overlap", new Newtonsoft.Json.Linq.JObject {
            ["confirmedHuman"] = m_RecordingInputConfirmed, ["speakerLearning"] = false,
            ["scope"] = "raw PCM retained; partial echo guard and strict barge-in remain separate" });
    }

    private void TraceInputEvidence(string stage, Newtonsoft.Json.Linq.JObject fields)
    {
        if (m_InputEvidenceCount++ >= 32) return;
        if (m_InputEvidenceTrace == null)
            m_InputEvidenceTrace = new ConversationTrace("capture-evidence", line => Debug.Log("[ConversationTrace] " + line));
        fields["captureSerial"] = CurrentCaptureSerial;
        m_InputEvidenceTrace.Sample("input." + stage, fields);
    }

    private void TraceInputProbeEvidence(bool bargeIn, bool speech,
        SenseVoiceSpeechToText.VoiceActivityResult vad, ManagedEchoCanceller.Result aec)
    {
        if (Time.realtimeSinceStartup < m_NextInputProbeTraceAt) return;
        m_NextInputProbeTraceAt = Time.realtimeSinceStartup + 2f;
        TraceInputEvidence("vad_probe", new Newtonsoft.Json.Linq.JObject {
            ["bargeIn"] = bargeIn, ["speech"] = speech, ["speechMs"] = vad?.SpeechMs ?? 0,
            ["tonalOverride"] = vad?.IsSinging ?? false, ["speakerKind"] = vad?.SpeakerKind ?? "",
            ["speakerStatus"] = vad?.SpeakerStatus ?? "", ["aecApplied"] = aec.Applied,
            ["aecCorrelation"] = aec.Correlation, ["aecInputRms"] = aec.InputRms,
            ["aecOutputRms"] = aec.OutputRms, ["rms"] = m_SmoothedRms,
            ["noiseUpper"] = EffectiveNoiseUpper });
    }

    private void ResetRecordingInputConfirmation()
    {
        m_RecordingInputConfirmed = false;
        m_ConfirmationLastAudioMs = m_ConfirmationMelodyAudioMs = -1;
        m_ConfirmationMelodyFrames = 0;
    }

    private void ConfirmRecordingInput(string evidence)
    {
        if (m_RecordingInputConfirmed) return;
        m_RecordingInputConfirmed = true;
        TraceInputEvidence("confirmed", new Newtonsoft.Json.Linq.JObject {
            ["basis"] = evidence, ["rms"] = m_SmoothedRms, ["noiseUpper"] = EffectiveNoiseUpper,
            ["outputOverlap"] = m_RecordingOutputOverlap });
        m_ChatSample?.NotifyUserStartedSpeaking();
        if (m_LogTimings) Debug.Log($"[Input/Candidate] capture={CurrentCaptureSerial} confirmed={evidence}");
    }

    private void ObserveRecordingInputConfirmation(
        SenseVoiceSpeechToText.StreamingTranscript transcript, bool outputProtected)
    {
        if (!m_IsRecording || m_RecordingInputConfirmed || transcript == null) return;
        if (outputProtected)
        {
            // Playback observations cannot accumulate authority for a later
            // interruption when the echo guard ends, even if the packet repeats.
            m_ConfirmationMelodyFrames = 0;
            m_ConfirmationMelodyAudioMs = -1;
            m_ConfirmationLastAudioMs = Mathf.Max(m_ConfirmationLastAudioMs, transcript.AudioMs);
            return;
        }
        if (transcript.AudioMs <= m_ConfirmationLastAudioMs) return;
        m_ConfirmationLastAudioMs = transcript.AudioMs;
        float now = Time.realtimeSinceStartup;
        float audioEndTime = m_LastStreamAudioSubmittedAt < 0f ? -1f :
            m_LastStreamAudioSubmittedAt - Mathf.Max(0f, m_StreamSubmittedSeconds - transcript.AudioMs / 1000f);
        bool currentTail = transcript.ActivityAvailable && transcript.ActivityWindowMs >= 200 &&
            transcript.ActivityWindowMs <= 1000 && audioEndTime >= 0f && audioEndTime <= now &&
            now - audioEndTime <= 2f &&
            transcript.AudioMs - transcript.ActivityWindowMs >= m_ContinuationJoinSeconds * 1000f;
        float noiseUpper = EffectiveNoiseUpper;
        bool speechObserved = currentTail && transcript.ActivityVadAvailable &&
            transcript.ActivitySpeechMs >= 120 && transcript.ActivitySpeechEndAgeMs >= 0 &&
            transcript.ActivitySpeechEndAgeMs <= transcript.ActivityWindowMs &&
            transcript.ActivityRms > noiseUpper * 1.15f;
        if (speechObserved)
        {
            ConfirmRecordingInput("stream-speech-vad");
            return;
        }
        // Use the same current periodic/voiced/above-noise support as
        // TurnActivityEvidence, independently of full-clip IsSinging and the
        // EOU protection counter preloaded by likelySinging at recording start.
        bool strongMelody = m_EnableSingingMode && currentTail &&
            transcript.ActivityRms >= Mathf.Max(0.001f, noiseUpper * 1.35f) &&
            transcript.ActivityPeriodicity >= 0.70f && transcript.ActivityVoicedRatio >= 0.60f &&
            m_TurnActivity.RecentRms >= Mathf.Max(noiseUpper * 1.2f, transcript.ActivityRms * 0.5f);
        if (!strongMelody)
        {
            m_ConfirmationMelodyFrames = 0;
            m_ConfirmationMelodyAudioMs = -1;
            return;
        }
        if (m_ConfirmationMelodyAudioMs >= 0 && transcript.AudioMs - m_ConfirmationMelodyAudioMs > 2500)
            m_ConfirmationMelodyFrames = 0;
        // Text revisions or almost identical windows are not three independent
        // observations. Each counted current-tail sample must advance >=200ms.
        if (m_ConfirmationMelodyAudioMs >= 0 && transcript.AudioMs - m_ConfirmationMelodyAudioMs < 200)
            return;
        m_ConfirmationMelodyAudioMs = transcript.AudioMs;
        if (++m_ConfirmationMelodyFrames >= 3)
            ConfirmRecordingInput("sustained-current-stream-melody");
    }

    private void StartRecording(
        int forcedStartPos = -1,
        bool allowSpeakerLearning = true,
        bool likelySinging = false,
        float singingProbability = 0f,
        bool confirmedSpeech = true)
    {
        DiscardRejectedUnheardCapture();
        bool resumeCapture = CanResumeUnheardCapture(
            m_UnheardCapture.Count > 0, Time.realtimeSinceStartup - m_UnheardCaptureClosedAt,
            m_ChatSample != null && (m_ChatSample.IsVoiceOutputPlaying ||
                m_ChatSample.VoiceOutputRevision != m_UnheardVoiceRevision),
            m_UnheardMic == m_RecordedClip);
        m_ContinuationJoinSeconds = resumeCapture ? m_UnheardFrames / (float)Mathf.Max(1, m_UnheardFrequency) : 0f;
        m_InheritedFinalCapture = resumeCapture && m_NeuralVadClient != null &&
            m_NeuralVadClient.LastFinalCaptureSerial == m_UnheardCaptureSessionSerial &&
            (m_NeuralVadClient.LastFinalTranscriptSource == "full-asr" ||
             m_NeuralVadClient.LastFinalTranscriptSource == "full-preview" ||
             m_NeuralVadClient.LastFinalTranscriptSource == "closed-stream-recovery")
            ? m_UnheardCaptureSessionSerial : 0;
        m_InheritedFinalText = m_InheritedFinalCapture > 0 ? m_NeuralVadClient.LastFinalRawText : null;
        m_InheritedFinalMicrophone = m_InheritedFinalCapture > 0 ? m_RecordedClip : null;
        DiscardRecentAmbientSamples(m_AmbientSpeechRollbackSeconds);
        if (m_LogTimings)
        {
            float learnedStart = ResolveAdaptiveStartThreshold(
                m_SilenceThreshold, m_AmbientRmsFloor, m_AmbientNoiseCalibrated,
                m_QuietStartThreshold, m_AmbientStartMultiplier,
                m_MaxAdaptiveStartThreshold);
            float learnedEou = EffectiveQuietThreshold;
            Debug.Log($"[RMS/Adaptive] recording-start floor={m_AmbientRmsFloor:F4} " +
                      $"calibrated={m_AmbientNoiseCalibrated} " +
                      $"start={learnedStart:F4} eou={learnedEou:F4} " +
                      $"samples={m_AmbientRmsSamples.Count}");
        }
        ResetStalledTurnState();
        if (m_AwaitingTentativeFinal)
            AbandonAwaitingTentativeFinal();
        else
            ClearTentativeReuseState(true);
        // A resumed continuation is a new capture boundary from the ASR
        // scheduler's perspective, so it gets one fresh prefetch budget.
        m_TentativeFullPreviewAttempted = false;
        m_TentativeFullPreviewRetrySuppressed = false;
        ResetNeuralVadGate("recording-started");
        m_StreamHumBackPrefixOffered = false;
        bool resumedPlaybackOverlap = resumeCapture && m_RecordingOutputOverlap;
        m_CurrentRecordingAllowsSpeakerLearning = allowSpeakerLearning && !resumedPlaybackOverlap;
        m_LikelySinging = m_EnableSingingMode && likelySinging;
        m_MelodicEouProtectionActive = m_EnableSingingMode && likelySinging;
        m_MelodicEouEvidenceFrames = likelySinging ? 3 : 0;
        m_MelodicEouLowFrames = 0;
        m_LastMelodicEouEvidenceAudioMs = -1;
        m_RecordingRescuedByTonalOverride = likelySinging;
        m_CurrentSingingProbability = singingProbability;
        m_SilenceTimer = 0.0f; // 重置静默计时器
        m_IsRecording = true;
        m_TentativeCompletePending = false;
        m_TentativeCompleteText = "";
        m_TentativeUsedFullAsr = false;
        m_TentativeFullAsrResultReady = false;
        m_TentativeFullAsrText = "";
        m_TentativeFullAsrRawText = "";
        PrintLog(m_LikelySinging ? "正在倾听演唱..." : "正在录制对话...");

        if (m_NeuralVadClient != null)
        {
            m_NeuralVadClient.BeginLiveRecordingCandidateSession();
            if (resumeCapture)
                m_NeuralVadClient.SetInputCaptureOverlap(m_UnheardCaptureSessionSerial,
                    m_UnheardFrames / (float)Mathf.Max(1, m_UnheardFrequency));
        }
        m_RecordingFirstCaptureSerial = resumeCapture ? m_UnheardFirstCaptureSerial : CurrentCaptureSerial;
        personalLearningRecorder.Begin();
        m_InheritedContinuationCapture = m_InheritedFinalCapture > 0 ? CurrentCaptureSerial : 0;
        m_RecordingFirstCloseUtc = resumeCapture ? m_UnheardFirstCloseUtc : null;

        //用户主动开口——通知 agent loop：撤销待 tick、清零连续 AI 轮次计数。
        //即将到来的用户文本会自动触发新一轮 LLM 调用(走 SendData → PrepareUserTurn → StartStreaming)。
        ResetRecordingInputConfirmation();
        m_RecordingOutputOverlap = resumedPlaybackOverlap;
        m_InputEvidenceCount = 0;
        m_InputEvidenceTrace = new ConversationTrace("capture-evidence", line => Debug.Log("[ConversationTrace] " + line));
        TraceInputEvidence("candidate", new Newtonsoft.Json.Linq.JObject {
            ["speechVad"] = confirmedSpeech && !likelySinging, ["tonalOverride"] = likelySinging,
            ["noiseCalibrated"] = m_AmbientNoiseCalibrated, ["noiseUpper"] = EffectiveNoiseUpper,
            ["rms"] = m_SmoothedRms, ["startThreshold"] = ResolveAdaptiveStartThreshold(
                m_SilenceThreshold, m_AmbientRmsFloor, m_AmbientNoiseCalibrated,
                m_QuietStartThreshold, m_AmbientStartMultiplier, m_MaxAdaptiveStartThreshold),
            ["quietThreshold"] = EffectiveQuietThreshold });
        if (resumeCapture)
            TraceInputEvidence("continuation", new Newtonsoft.Json.Linq.JObject {
                ["previousCaptureSerial"] = m_UnheardCaptureSessionSerial,
                ["firstCaptureSerial"] = m_RecordingFirstCaptureSerial,
                ["firstCaptureRetainedUtc"] = m_RecordingFirstCloseUtc,
                ["previousCaptureRetainedUtc"] = m_UnheardCloseUtc,
                ["scope"] = "recorder handoff UTC; use firstCaptureSerial eot.capture_closed for exact audited latency"
            });
        m_ChatSample?.NotifyUserRecordingCandidate();
        if (confirmedSpeech && !likelySinging) ConfirmRecordingInput("speech-vad");
        else if (m_LogTimings)
            Debug.Log("[Input/Candidate] 仅启动音频取证；弱调性或RMS候选不撤销正在准备/播放的回复");

        //★ 关键：不再End/Start mic！老逻辑那一刻会丢掉触发帧的音节
        //  ("换个话题吧"被识别成"个话题吧"就是这个原因)。
        //  现在mic从应用启动起就一直跑(loop=true 30s ringbuffer)，
        //  这里只需在ring buffer里标个起始位置——并往回拨pre-roll把
        //  VAD滞后期间已经被录到、但还没触发的开头音节抢回来。
        // 兜底：mic 意外停了就按已选择的设备重启；不会回退到
        // VoiceInputs 的默认 Microphone.Start(null) 路径。
        if (!TryEnsureMicrophone(out int ensuredPos))
        {
            // The VAD coroutine normally guarantees a live device before this
            // point. If the device disappears between frames, unwind the
            // capture state instead of leaving IsRecording latched forever.
            m_IsRecording = false;
            m_RecordingStartPos = -1;
            personalLearningRecorder.Seal(null);
            ResetRecordingCapture();
            return;
        }

        int curPos = ensuredPos;
        int totalSamples = m_RecordedClip != null ? m_RecordedClip.samples : 16000 * 30;
        m_RecordingStartPos = forcedStartPos >= 0
            ? Mathf.Clamp(forcedStartPos, 0, totalSamples - 1)
            : CalculateRecordingStartPos(curPos);
        if (!resumeCapture && m_RejectedContinuationMic == m_RecordedClip &&
            Time.realtimeSinceStartup - m_RejectedContinuationClosedAt >= 0f &&
            Time.realtimeSinceStartup - m_RejectedContinuationClosedAt <= 3f)
            m_RecordingStartPos = RecordingContinuationIsolation.ClampPreRoll(
                m_RecordingStartPos, curPos, m_RejectedContinuationEndPos, totalSamples);

        BeginRecordingCapture(m_RecordingStartPos, curPos);
        m_RecordingCapturedAt = Time.realtimeSinceStartup;
        if (resumeCapture)
        {
            // The authoritative original is being reconstructed from an older prefix.
            // Recompute its pitch from the sealed WAV instead of reusing a partial stream.
            personalLearningRecorder.CancelPitch();
            // The ring cursor now marks the join, not the beginning of the song.
            // Full PCM analysis remains available; do not pre-convert a tail as a prefix.
            m_StreamHumBackPrefixOffered = true;
            // Use the exact previous endpoint, not new pre-roll, so no samples repeat.
            ResetRecordingCapture();
            m_RecordingPcmChunks.AddRange(m_UnheardCapture);
            m_RecordingCapturedFrames = m_UnheardFrames;
            m_RecordingCaptureChannels = m_UnheardChannels;
            m_RecordingCaptureFrequency = m_UnheardFrequency;
            m_RecordingCaptureLastPos = m_UnheardEndPos;
            m_RecordingCapturedAt = m_UnheardCapturedAt;
            m_RecordingStartPos = m_UnheardEndPos;
            CaptureRecordingAudio(curPos, true);
            Debug.Log("[Listening/Continuation] 首次回复前声音恢复，合并原始录音与续音；不把歌词尾音另立一轮");
        }
        m_ChatSample?.TraceCaptureStarted(CurrentCaptureSerial, resumeCapture, m_RecordingRescuedByTonalOverride);
        m_NeuralVadClient?.SetInputCaptureTime(m_RecordingCapturedAt);
        m_ClosedSpeechTracker.Begin(CurrentCaptureSerial, m_RecordingCapturedAt);
        m_UnheardCapture.Clear();
        // Keep the same ASR stream during the brief, unheard handoff so its
        // cumulative transcript still contains the beginning, not only the tail.
        if (resumeCapture && m_StreamLastSentPos >= 0)
            PumpStreamingAudio(curPos, true);
        else
            BeginStreamingRecognition(curPos);
    }
    /// <summary>
    /// 结束说话
    /// </summary>
    private void StopRecording(bool reuseTentativeFullAsr = false)
    {
        float stopNow = Time.realtimeSinceStartup;
        ObserveBoundaryRequestDuration(stopNow, false);
        m_ChatSample?.TraceCaptureStopRequested(
            CurrentCaptureSerial,
            m_RecordingCloseReason,
            m_RecordingCapturedAt >= 0f ? stopNow - m_RecordingCapturedAt : -1f,
            m_StreamSubmittedSeconds,
            m_RecordingCapturedFrames,
            MicrophoneIsRecording(m_ActiveMicrophoneName));
        EndPendingSemanticEou("recording_closed:" + m_RecordingCloseReason);
        HoldAmbientLearningAtHandoff();
        m_IsRecording = false;
        bool allowSpeakerLearning = m_CurrentRecordingAllowsSpeakerLearning;
        m_CurrentRecordingAllowsSpeakerLearning = true;

        PrintLog("会话录制结束...");

        //★ mic不再End/Start：保持continuous loop=true运行(StartRecording时已用m_RecordingStartPos标好起点)。
        //  从ring buffer里截出[m_RecordingStartPos, currentPos)给ASR——这段就是用户的整段发言
        //  (含开头pre-roll，避免首音节被切掉)。老逻辑那一刻End/Start会丢触发帧的音节。
        int curPos = GetMicrophonePosition(m_ActiveMicrophoneName);
        CaptureRecordingAudio(curPos, true);
        PumpStreamingAudio(curPos, true);
        RetainUnheardCapture(curPos);
        string stablePartialForPromotion = "";
        bool canPromoteStablePartial = !reuseTentativeFullAsr &&
            TryGetDispatchableStablePartial(curPos, out stablePartialForPromotion);
        AudioClip toSend = BuildAccumulatedRecordingClip();
        if (toSend == null && m_RecordingStartPos >= 0)
            toSend = SnapshotFromBuffer(m_RecordingStartPos, curPos);
        SealPersonalLearningCapture(toSend, true);
        if (toSend == null) canPromoteStablePartial = false;
        if (m_UnheardCapture.Count == 0) EndStreamingRecognition();
        m_RecordingStartPos = -1;

        //兜底：mic若被外部停掉就重启，确保后续idle期VAD/barge-in仍有数据
        TryEnsureMicrophone(out _);
        //AI说话期间不锁定——VAD要靠m_BargeInTimer工作，m_LockState只在录用户正经发言时用
        m_LockState = false;

        //把截好的clip送给ChatSample做ASR/LLM/TTS
        if (m_ChatSample != null)
        {
            PublishListeningEndEvidence();
            PublishClosedSpeechEvidence();
            m_ChatSample.CancelTurnBoundaryReview();
            //EOU锚点：必须在AcceptClip之前调用，ChatSample的DealingTextCallback/StartStreaming
            //会用这个时间戳算ASR延迟和"EOU→首音"总延迟。
            m_ChatSample.MarkEOU(m_RecordingRescuedByTonalOverride);
            bool partialHeld = canPromoteStablePartial &&
                m_ChatSample.AcceptStablePartialText(stablePartialForPromotion);
            if (m_LogTimings)
            {
                float clipLen = toSend != null ? toSend.length : 0f;
                string dispatchState = reuseTentativeFullAsr
                    ? "复用/等待同轮预测ASR"
                    : partialHeld
                        ? "稳定partial已保留作预判；正式等待完整ASR"
                        : "已发送最终ASR";
                Debug.Log($"[Timing] EOU 触发 (程序收束采集) — clip长度 {clipLen:F2}s, " +
                          dispatchState);
            }
            if (reuseTentativeFullAsr && m_TentativeFullAsrResultReady)
            {
                bool streamingExit = m_ChatSample.BeginDeferredFinalAsr(toSend);
                if (m_LogTimings)
                    Debug.Log("[T-EOU] 复用已完成的完整预测ASR作为最终结果；跳过重复识别");
                m_NeuralVadClient?.PublishReusedFinalTranscript(CurrentCaptureSerial, m_TentativeFullAsrRawText,
                    m_TentativeFullAsrText);
                m_ChatSample.AcceptDeferredFinalAsrText(
                    m_TentativeFullAsrText,
                    streamingExit);
                //BeginDeferredFinalAsr 已同步提取了需要保留的音频字节；
                //本轮直接采用预测文本后，临时 AudioClip 不再有消费者。
                if (toSend != null)
                    Destroy(toSend);
                ClearTentativeReuseState(true);
            }
            else if (reuseTentativeFullAsr && m_TentativePreviewInFlight)
            {
                m_ChatSample.PromotePreviewAnalysis();
                m_AwaitingTentativeFinal = true;
                m_AwaitingTentativeSeq = m_TentativeSeq;
                m_AwaitingTentativeFinalClip = toSend;
                m_AwaitingTentativeAllowSpeakerLearning = allowSpeakerLearning;
                m_AwaitingTentativeStreamingExit =
                    m_ChatSample.BeginDeferredFinalAsr(toSend);
                if (m_AwaitingTentativeFallbackCoroutine != null)
                    StopCoroutine(m_AwaitingTentativeFallbackCoroutine);
                m_AwaitingTentativeFallbackCoroutine = StartCoroutine(
                    AwaitTentativeFinalOrFallback(m_AwaitingTentativeSeq));
                if (m_LogTimings)
                    Debug.Log($"[T-EOU] 硬EOU等待同轮预测ASR晋升最终结果 " +
                              $"seq={m_AwaitingTentativeSeq} timeout=" +
                              $"{m_TentativeFinalReuseTimeoutSeconds:F1}s");
            }
            else
            {
                m_ChatSample.AcceptClip(toSend, allowSpeakerLearning);
            }
        }
        m_LikelySinging = false;
        ResetMelodicEouProtection();
        m_RecordingRescuedByTonalOverride = false;
        m_CurrentSingingProbability = 0f;
        ResetStalledTurnState();
    }

    private void BeginRecordingCapture(int startPos, int currentPos)
    {
        ResetRecordingCapture();
        if (m_RecordedClip == null || startPos < 0) return;
        m_RecordingCaptureChannels = Mathf.Max(1, m_RecordedClip.channels);
        m_RecordingCaptureFrequency = Mathf.Max(1, m_RecordedClip.frequency);
        m_RecordingCaptureLastPos = startPos;
        CaptureRecordingAudio(currentPos, true);
    }

    private static bool CanResumeUnheardCapture(bool hasAudio, float gap, bool replied, bool sameMic) =>
        hasAudio && sameMic && !replied && gap >= 0f && gap <= 3f;

    private void DiscardRejectedUnheardCapture()
    {
        if (m_UnheardCapture.Count == 0 || m_NeuralVadClient == null ||
            !m_NeuralVadClient.IsCaptureRejectedSelfSpeech(m_UnheardCaptureSessionSerial)) return;
        m_RejectedContinuationMic = m_UnheardMic;
        m_RejectedContinuationEndPos = m_UnheardEndPos;
        m_RejectedContinuationClosedAt = m_UnheardCaptureClosedAt;
        m_UnheardCapture.Clear();
        m_UnheardMic = null;
        // Clear the cumulative partial stream as well as PCM. New speech still
        // starts normally, including barge-in while external speakers are active.
        if (!m_IsRecording) EndStreamingRecognition();
        Debug.Log($"[Listening/Continuation] rejected self-speech capture={m_UnheardCaptureSessionSerial}; " +
            "discard old PCM/stream, preserve fresh input after its endpoint");
    }

    private void RetainUnheardCapture(int endPos)
    {
        m_UnheardCapture.Clear();
        if (m_RecordingCloseReason == "manual-disable" || m_RecordingCloseReason == "llm-interrupt_user")
            return;
        m_UnheardCapture.AddRange(m_RecordingPcmChunks);
        m_UnheardCaptureClosedAt = Time.realtimeSinceStartup;
        m_UnheardCloseUtc = DateTime.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(m_RecordingFirstCloseUtc)) m_RecordingFirstCloseUtc = m_UnheardCloseUtc;
        m_UnheardFirstCloseUtc = m_RecordingFirstCloseUtc;
        m_UnheardFirstCaptureSerial = m_RecordingFirstCaptureSerial;
        m_UnheardCapturedAt = m_RecordingCapturedAt;
        m_UnheardCaptureSessionSerial = m_NeuralVadClient != null ? m_NeuralVadClient.InputCaptureSessionSerial : 0;
        m_UnheardEndPos = endPos;
        m_UnheardFrames = m_RecordingCapturedFrames;
        m_UnheardChannels = m_RecordingCaptureChannels;
        m_UnheardFrequency = m_RecordingCaptureFrequency;
        m_UnheardMic = m_RecordedClip;
        m_UnheardVoiceRevision = m_ChatSample != null ? m_ChatSample.VoiceOutputRevision : -1;
    }

    private void AbandonAwaitingTentativeFinal()
    {
        if (!m_AwaitingTentativeFinal) return;
        if (m_AwaitingTentativeFallbackCoroutine != null) StopCoroutine(m_AwaitingTentativeFallbackCoroutine);
        m_AwaitingTentativeFallbackCoroutine = null;
        if (m_AwaitingTentativeFinalClip != null) Destroy(m_AwaitingTentativeFinalClip);
        m_AwaitingTentativeFinalClip = null;
        m_AwaitingTentativeFinal = false;
        m_AwaitingTentativeSeq = -1;
        m_ChatSample?.AbandonDeferredFinalAsr();
        ClearTentativeReuseState(true);
    }

    private void CaptureRecordingAudio(int currentPos, bool force)
    {
        if (m_RecordedClip == null || m_RecordingCaptureLastPos < 0) return;
        if (!force && Time.realtimeSinceStartup < m_NextRecordingCaptureTime) return;
        m_NextRecordingCaptureTime = Time.realtimeSinceStartup +
            k_RecordingCaptureIntervalSeconds;
        if (currentPos == m_RecordingCaptureLastPos) return;

        float[] samples = CopySamplesFromBuffer(m_RecordingCaptureLastPos, currentPos);
        m_RecordingCaptureLastPos = currentPos;
        if (samples == null || samples.Length == 0) return;
        int channels = Mathf.Max(1, m_RecordingCaptureChannels);
        int frames = samples.Length / channels;
        if (frames <= 0) return;
        m_RecordingPcmChunks.Add(samples);
        m_RecordingCapturedFrames += frames;
        personalLearningRecorder.PushPitch(samples, channels, m_RecordingCaptureFrequency);
    }

    private AudioClip BuildAccumulatedRecordingClip(bool consume = true)
    {
        if (m_RecordingCapturedFrames <= 0 || m_RecordingPcmChunks.Count == 0)
        {
            if (consume) ResetRecordingCapture();
            return null;
        }

        int channels = Mathf.Max(1, m_RecordingCaptureChannels);
        int frequency = Mathf.Max(1, m_RecordingCaptureFrequency);
        int sampleCount = m_RecordingCapturedFrames * channels;
        var combined = new float[sampleCount];
        int offset = 0;
        for (int i = 0; i < m_RecordingPcmChunks.Count && offset < sampleCount; i++)
        {
            float[] chunk = m_RecordingPcmChunks[i];
            if (chunk == null || chunk.Length == 0) continue;
            int copy = Mathf.Min(chunk.Length, sampleCount - offset);
            System.Array.Copy(chunk, 0, combined, offset, copy);
            offset += copy;
        }

        int frames = offset / channels;
        AudioClip clip = null;
        if (frames > 0)
        {
            clip = AudioClip.Create("rt_accumulated", frames, channels, frequency, false);
            if (!clip.SetData(combined, 0))
            {
                Destroy(clip);
                clip = null;
            }
            else if (m_LogTimings)
            {
                Debug.Log($"[RTSpeech/PCM] 完整录音累计 {frames / (float)frequency:F2}s " +
                          $"({m_RecordingPcmChunks.Count}块)，不受 " +
                          $"{m_MicrophoneBufferSeconds}s 环形缓冲覆盖");
            }
        }
        if (consume) ResetRecordingCapture();
        return clip;
    }

    private void ResetRecordingCapture()
    {
        m_RecordingPcmChunks.Clear();
        m_RecordingCaptureLastPos = -1;
        m_RecordingCapturedFrames = 0;
        m_RecordingCaptureChannels = 1;
        m_RecordingCaptureFrequency = 16000;
        m_NextRecordingCaptureTime = 0f;
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

    // Unlike the semantic key, this revision includes punctuation, negation repairs,
    // empty ASR revisions and rollbacks. Returning to an old spelling cannot revive a lease.
    private string m_PreparedBoundaryRawTranscript = "";
    private int m_PreparedBoundaryTextRevision;

    private void PublishPreparedTurnBoundaryEvidence()
    {
        if (m_ChatSample == null) return;
        float now = Time.realtimeSinceStartup;
        m_ChatSample.ObservePreparedTurnBoundaryEvidence(new PreparedTurnBoundaryEvidence
        {
            CaptureSerial = CurrentCaptureSerial,
            TextRevision = m_PreparedBoundaryTextRevision,
            ActivityRevision = m_TurnActivity.ActivityRevision,
            Transcript = m_PreparedBoundaryRawTranscript,
            AudioMs = m_LatestStreamPartialAudioMs,
            SampledAt = now,
            LatestSoundAt = m_TurnActivity.LatestSoundAt,
            QuietSeconds = m_TurnActivity.QuietSeconds(now),
            MinimumQuietSeconds = m_TurnBoundaryDropHoldSeconds,
            MinimumConfidence = m_StalledTurnDecisionMinConfidence,
            Recording = m_IsRecording,
            AsrHealthy = m_TurnActivity.AsrHealthy(now) &&
                m_LastStreamProgressTime >= 0f && now - m_LastStreamProgressTime <= 2.5f,
            FreshCoverage = HasFreshStreamingCoverage,
            CoversQuietTail = m_TurnActivity.CoversQuietTail,
            PendingText = m_TurnActivity.PendingText,
            LevelActive = m_TurnActivity.LevelActive,
            MelodyActive = m_TurnActivity.MelodyActive,
            TextActivityActive = m_TurnActivity.TextActivityActive,
            TailAvailable = m_TurnActivity.TailAvailable,
            TailAge = m_TurnActivity.TailAge(now),
            Singing = m_LikelySinging || m_MelodicEouProtectionActive || m_RecordingRescuedByTonalOverride,
        });
    }

    /// <summary>
    /// 建立一轮 WebSocket partial 会话，并立即补发 pre-roll 到当前麦克风位置。
    /// </summary>
    private void BeginStreamingRecognition(int currentPos)
    {
        m_PreparedBoundaryRawTranscript = "";
        m_PreparedBoundaryTextRevision++;
        m_StreamLastSentPos = -1;
        m_StreamSubmittedSeconds = 0f;
        m_LastStreamAudioSubmittedAt = -1f;
        m_LatestStreamPartial = "";
        m_LatestStreamPartialAudioMs = 0;
        m_LatestStreamPartialTime = -1f;
        m_LatestStreamStablePartial = "";
        m_LatestStreamStablePartialAudioMs = 0;
        m_LatestStreamStablePartialTime = -1f;
        m_LastStreamProgressTime = -1f;
        m_NextStreamAudioPushTime = 0f;
        m_StreamingStartedAt = -1f;
        m_StreamingTerminalAt = -1f;
        m_StreamingTransportState = "idle";
        m_StreamingTransportReason = "";
        m_StreamingTransportGeneration = -1;
        m_StreamingPushStopped = false;

        if (!m_EnableStreamingRecognition || m_NeuralVadClient == null ||
            !m_NeuralVadClient.StreamingPreviewEnabled || m_RecordingStartPos < 0)
            return;

        bool started = m_NeuralVadClient.BeginStreamingPreview(OnStreamingTranscript);
        if (!started) return;
        m_StreamingTransportGeneration = m_NeuralVadClient.StreamingGeneration;
        m_StreamingStartedAt = Time.realtimeSinceStartup;
        m_StreamingTransportState = "starting";
        m_StreamLastSentPos = m_RecordingStartPos;
        if (m_LogTimings)
            Debug.Log($"[Listening/ASR] partial coverage policy capture={CurrentCaptureSerial} " +
                      $"lag<={m_StreamPartialMaxLagMs}ms age<={m_StreamPartialMaxAgeSeconds:F2}s " +
                      $"coverageWait<={m_CoverageWaitTimeoutSeconds:F2}s");
        PumpStreamingAudio(currentPos, true);
    }

    private void EndStreamingRecognition()
    {
        if (m_NeuralVadClient != null) m_NeuralVadClient.CancelStreamingPreview();
        m_StreamLastSentPos = -1;
        m_NextStreamAudioPushTime = 0f;
        m_StreamingPushStopped = true;
    }

    private void HandleStreamingTransportChanged(int generation, string state, string reason)
    {
        if (generation != m_StreamingTransportGeneration) return;
        if (!m_IsRecording && state != "cancelled") return;

        float now = Time.realtimeSinceStartup;
        m_StreamingTransportState = state ?? "";
        m_StreamingTransportReason = reason ?? "";
        if (state == "started" && m_StreamingStartedAt < 0f)
            m_StreamingStartedAt = now;
        if (state == "closed" || state == "error")
        {
            m_StreamingTerminalAt = now;
            // Keep collecting the original microphone PCM for final ASR, but do
            // not keep feeding a dead preview transport.
            m_StreamingPushStopped = true;
        }

        m_ChatSample?.TraceCaptureStreamChanged(
            CurrentCaptureSerial,
            state,
            reason,
            generation,
            m_StreamSubmittedSeconds,
            m_RecordingCapturedAt >= 0f ? now - m_RecordingCapturedAt : -1f);
        if (m_LogTimings && (state == "closed" || state == "error"))
            Debug.LogWarning($"[Listening/ASR] 流式传输 {state} reason={reason}；保留原始PCM并等待收束 watchdog");
    }

    private bool TryApplyCaptureWatchdog(float now)
    {
        if (!m_IsRecording) return false;

        float captureAge = m_RecordingCapturedAt >= 0f
            ? Mathf.Max(0f, now - m_RecordingCapturedAt)
            : 0f;
        string reason = "";
        float triggerAge = captureAge;
        if (m_MaxRecordingSeconds > 0f && captureAge >= m_MaxRecordingSeconds)
        {
            reason = "capture-watchdog";
        }
        else if (m_EnableStreamingRecognition && m_StreamingStartedAt >= 0f)
        {
            float terminalAge = m_StreamingTerminalAt >= 0f
                ? now - m_StreamingTerminalAt : -1f;
            float progressAge = m_LastStreamProgressTime >= 0f
                ? now - m_LastStreamProgressTime
                : now - m_StreamingStartedAt;
            bool submittedEnough = m_StreamSubmittedSeconds >= 2f;
            if (terminalAge >= Mathf.Max(0.2f, m_AsrStreamTerminalGraceSeconds))
            {
                reason = "asr-stream-stalled";
                triggerAge = terminalAge;
            }
            else if (submittedEnough && progressAge >= Mathf.Max(2f, m_AsrStreamStallSeconds))
            {
                reason = "asr-stream-stalled";
                triggerAge = progressAge;
            }
        }

        if (string.IsNullOrEmpty(reason)) return false;
        bool reuseTentativeFullAsr = m_TentativeUsedFullAsr &&
            (m_TentativePreviewInFlight || m_TentativeFullAsrResultReady);
        m_RecordingCloseReason = reason;
        m_ChatSample?.TraceCaptureStopRequested(
            CurrentCaptureSerial,
            reason,
            captureAge,
            m_StreamSubmittedSeconds,
            m_RecordingCapturedFrames,
            MicrophoneIsRecording(m_ActiveMicrophoneName));
        m_ChatSample?.TraceCaptureWatchdog(
            CurrentCaptureSerial,
            reason,
            triggerAge,
            m_StreamingTransportState,
            m_StreamingTransportReason,
            m_StreamSubmittedSeconds);
        if (m_LogTimings)
            Debug.LogWarning($"[Listening/Watchdog] 收束 capture reason={reason} " +
                $"captureAge={captureAge:F1}s triggerAge={triggerAge:F1}s " +
                $"stream={m_StreamingTransportState}/{m_StreamingTransportReason} " +
                $"submitted={m_StreamSubmittedSeconds:F1}s");
        // RetainUnheardCapture intentionally keeps PCM for a possible short
        // handoff, which normally also keeps a healthy preview stream alive.
        // A watchdog conclusion is different: the preview has no trustworthy
        // progress, so terminate it before the handoff rather than carrying a
        // dead socket into the next capture. Final ASR still uses the chunks.
        EndStreamingRecognition();
        StopRecording(reuseTentativeFullAsr);
        return true;
    }

    private void ObserveStreamingTurnActivity(
        SenseVoiceSpeechToText.StreamingTranscript transcript, float now)
    {
        int acousticRevision = m_TurnActivity.ActivityRevision;
        bool textAdvanced = m_TurnActivity.ObserveAsr(
            NormalizeSemanticTranscript(transcript.Text), NormalizeSemanticTranscript(transcript.StableText),
            transcript.AudioMs, now,
            transcript.ActivityAvailable && transcript.ActivityWindowMs >= 200 && transcript.ActivityWindowMs <= 1000,
            transcript.ActivityRms, transcript.ActivityPeriodicity, transcript.ActivityVoicedRatio, EffectiveNoiseUpper,
            transcript.ActivityVadAvailable, transcript.ActivitySpeechMs, transcript.ActivitySpeechEndAgeMs,
            m_LastStreamAudioSubmittedAt < 0f ? -1f : m_LastStreamAudioSubmittedAt -
                Mathf.Max(0f, m_StreamSubmittedSeconds - transcript.AudioMs / 1000f));
        // VAD may confirm new sound even when the recognized words do not change.
        if (m_TurnActivity.ActivityRevision != acousticRevision)
            InvalidateTentativeEou("recent-tail-voice");
        if (textAdvanced)
        {
            TrackMeaningfulStreamText(transcript.Text, transcript.AudioMs);
            // The full preview already contains the old audio being revised. Only
            // new sound invalidates its coverage; partial-based guesses still expire.
            if (!m_TentativeUsedFullAsr || m_TurnActivity.ActivityRevision != acousticRevision)
                InvalidateTentativeEou("new-asr-content-with-new-audio");
            m_SilenceTimer = m_TurnActivity.QuietSeconds(now);
        }
        if ((!transcript.ActivityAvailable || !transcript.ActivityVadAvailable) && !m_ActivityProtocolWarningLogged)
        {
            m_ActivityProtocolWarningLogged = true;
            Debug.LogWarning("[Listening/Activity] 近期声学/VAD时间字段不可用；若持续出现请重启 SenseVoice。文字修订和旧歌唱概率不作为新发声证明。");
        }
    }

    private void OnStreamingTranscript(SenseVoiceSpeechToText.StreamingTranscript transcript)
    {
        if (!m_IsRecording || transcript == null) return;
        m_ChatSample?.TracePartialAsr(CurrentCaptureSerial, transcript);
        string rawTranscript = transcript.Text ?? "";
        if (!string.Equals(rawTranscript, m_PreparedBoundaryRawTranscript, StringComparison.Ordinal))
        {
            m_PreparedBoundaryRawTranscript = rawTranscript;
            m_PreparedBoundaryTextRevision++;
        }
        ObserveStreamingTurnActivity(transcript, Time.realtimeSinceStartup);
        bool speechObserved = transcript.ActivityAvailable && transcript.ActivityVadAvailable &&
            transcript.ActivityWindowMs >= 200 && transcript.ActivityWindowMs <= 1000 &&
            transcript.ActivitySpeechMs >= 120 && transcript.ActivitySpeechEndAgeMs >= 0 &&
            transcript.ActivityRms > EffectiveNoiseUpper * 1.15f;
        m_ClosedSpeechTracker.Observe(CurrentCaptureSerial, transcript.Text, transcript.StableText,
            transcript.AudioMs, Time.realtimeSinceStartup, speechObserved);
        float latestSingingProbability = Mathf.Clamp01(transcript.SingingProbability);
        m_LatestStreamingSingingProbability = latestSingingProbability;
        if (!m_HasStreamingSingingProbability)
        {
            m_RecentStreamingSingingProbability = latestSingingProbability;
            m_PreviousStreamingSingingProbability = latestSingingProbability;
            m_StreamingSingingProbabilityTrend = 0f;
            m_HasStreamingSingingProbability = true;
        }
        else
        {
            float frameDelta = latestSingingProbability -
                m_PreviousStreamingSingingProbability;
            //约五个流式帧的短期均值与趋势。它们描述“现在”，而不是把本轮早期峰值
            //一直冒充当前状态交给轮次判断。
            m_RecentStreamingSingingProbability = Mathf.Lerp(
                m_RecentStreamingSingingProbability,
                latestSingingProbability,
                0.22f);
            m_StreamingSingingProbabilityTrend = Mathf.Lerp(
                m_StreamingSingingProbabilityTrend,
                frameDelta,
                0.25f);
            m_PreviousStreamingSingingProbability = latestSingingProbability;
        }
        m_CurrentSingingProbability = Mathf.Max(
            m_CurrentSingingProbability,
            latestSingingProbability);
        m_LastStreamingPitchStability = Mathf.Clamp01(transcript.PitchStability);
        UpdateMelodicEouProtection(transcript);
        // Candidate recording is evidence collection. Only independent current
        // speech or sustained melody while no AI audio is present may supersede
        // a pending answer. Real barge-in keeps the stricter AEC/identity path.
        bool outputProtected = m_ChatSample != null &&
            m_ChatSample.IsAIPlaybackProtected(m_PostPlaybackEchoGuardSeconds);
        ObserveRecordingInputConfirmation(transcript, outputProtected);
        //ChatSample 会综合连续帧后通过 OnStableUserSingingObserved 回传稳定事实。
        //它存在时不能再由单个 p>=0.58 帧直接锁死歌唱 EOU；仅在没有语义协调器的
        //降级场景保留旧的纯声学入口。
        if (ShouldApplyDirectAcousticSingingLatch(
                m_ChatSample != null,
                m_EnableSingingMode,
                transcript.IsSinging,
                transcript.SingingProbability,
                m_SingingProbabilityThreshold))
        {
            HandleStableUserSingingObserved(
                transcript.SingingProbability,
                transcript.PitchStability,
                transcript.AudioMs);
        }
        if (!string.IsNullOrWhiteSpace(transcript.Text))
            m_LatestStreamPartial = transcript.Text.Trim();
        if (!string.IsNullOrWhiteSpace(transcript.StableText))
        {
            m_LatestStreamStablePartial = transcript.StableText.Trim();
            m_LatestStreamStablePartialAudioMs = transcript.AudioMs;
            m_LatestStreamStablePartialTime = Time.realtimeSinceStartup;
        }
        if (transcript.AudioMs > m_LatestStreamPartialAudioMs)
            m_LastStreamProgressTime = Time.realtimeSinceStartup;
        m_LatestStreamPartialAudioMs = transcript.AudioMs;
        m_LatestStreamPartialTime = Time.realtimeSinceStartup;
        PublishPreparedTurnBoundaryEvidence();
        if (m_ChatSample != null && m_RecordingInputConfirmed) m_ChatSample.UpdateStreamingTranscript(transcript);

        // A requested sing-along can hide almost all RVC latency behind the user's own
        // performance.  Once singing is stable and the prefix is long enough, snapshot
        // exactly the beginning of the ring-buffer recording and offer it only once.
        // Enforce the new seamless-handoff minimum at runtime as well, so an already
        // open scene that still holds the former serialized value (12 s) is safe.
        float humBackPrefixSeconds = Mathf.Max(20f, m_StreamHumBackPrefixSeconds);
        if (!m_StreamHumBackPrefixOffered && m_LikelySinging && m_ChatSample != null &&
            transcript.AudioMs >= Mathf.RoundToInt(humBackPrefixSeconds * 1000f) &&
            m_RecordedClip != null && m_RecordingStartPos >= 0 &&
            m_ChatSample.CanPrepareStreamingHumBackPrefix())
        {
            int prefixFrames = Mathf.Min(
                m_RecordedClip.samples - 1,
                Mathf.RoundToInt(humBackPrefixSeconds * m_RecordedClip.frequency));
            int availableFrames = RingSampleDistance(
                m_RecordingStartPos,
                GetMicrophonePosition(m_ActiveMicrophoneName),
                m_RecordedClip.samples);
            if (availableFrames >= prefixFrames)
            {
                int prefixEnd = (m_RecordingStartPos + prefixFrames) % m_RecordedClip.samples;
                AudioClip prefix = SnapshotFromBuffer(m_RecordingStartPos, prefixEnd);
                m_StreamHumBackPrefixOffered = prefix != null &&
                    m_ChatSample.TryPrepareStreamingHumBackPrefix(
                        prefix,
                        transcript.SingingProbability,
                        transcript.PitchStability);
                if (!m_StreamHumBackPrefixOffered && prefix != null) Destroy(prefix);
            }
        }
    }

    private void TrackMeaningfulStreamText(string text, int audioMs)
    {
        string trimmed = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        string key = NormalizeSemanticTranscript(trimmed);
        if (key.Length == 0) return;
        //The shared tracker has confirmed a real update, including one-character additions.

        m_LastMeaningfulStreamText = trimmed;
        m_LastMeaningfulStreamTextKey = key;
        m_LastMeaningfulStreamTextTime = Time.realtimeSinceStartup;
        m_LastMeaningfulStreamTextAudioMs = Mathf.Max(0, audioMs);
        m_StalledContinueDecisionCount = 0;
        m_LastStalledDecisionStatus = "";
        m_LastStalledDecisionConfidence = 0f;
        // Do not cancel an in-flight boundary decision for every ordinary ASR
        // suffix update. The callback re-checks the current text/activity lease
        // before it can close the capture; if the transcript changed, it rejects
        // the stale decision and the normal retry path uses the newest text.
        // Cancelling here created a serial request storm (request A cancelled by
        // suffix B, then B by suffix C) and pushed EOT several seconds later.
        bool boundaryRequestInFlight = m_StalledTranscriptReviewRequested;
        if (boundaryRequestInFlight && m_ChatSample != null)
        {
            m_ChatSample.TurnBoundaryTrace?.Mark("eou.request_coalesced", new Newtonsoft.Json.Linq.JObject {
                ["reason"] = "partial-suffix-update",
                ["textLength"] = trimmed.Length,
                ["audioMs"] = m_LastMeaningfulStreamTextAudioMs,
                ["scope"] = "in-flight boundary request retained; callback must revalidate current text/activity"
            });
        }
        EndPendingSemanticEou("transcript_changed");
        // Keep the existing boundary request alive while ASR appends a suffix.
        // Its completion path compares the captured text/activity revision with
        // the current turn and rejects stale decisions. Clearing this flag here
        // would allow every partial suffix to launch another request before the
        // prior one has returned.
        m_StalledTranscriptReviewRequested = boundaryRequestInFlight;
        m_PendingSemanticEouStatus = "";
        m_PendingSemanticEouTextKey = "";
        //等待时长由证据选择，不能在这里再串联一个固定的 2.8 秒门槛。
        m_NextStalledTranscriptReviewTime = Time.realtimeSinceStartup;
        m_StalledReviewFailures = 0;
        m_BoundaryCandidateTime = -1f;
    }

    private void MaybeRequestStalledTurnReview(
        float currentRms,
        float eouThreshold)
    {
        if (m_TentativeUsedFullAsr && m_TentativeFullAsrResultReady &&
            m_TurnActivity.ConfirmPendingText(m_TentativeFullAsrTranscriptKey,
                NormalizeSemanticTranscript(m_LatestStreamPartial), Time.realtimeSinceStartup))
        {
            TrackMeaningfulStreamText(m_LatestStreamPartial, m_LatestStreamPartialAudioMs);
            if (m_LogTimings) Debug.Log("[Semantic EOU] 完整ASR确认当前修订；复用已有静默，不等待第二个相同partial");
        }
        if (m_ChatSample == null || string.IsNullOrWhiteSpace(m_LastMeaningfulStreamText) ||
            m_PendingSemanticEou || m_TurnActivity.PendingText)
            return;
        float now = Time.realtimeSinceStartup;
        //独立边界请求仍可能因后端异常或正式轮次抢占而没有可用回调。不能让一个丢失的
        //回调永久锁死后续语义 EOU；超时后用新版本请求安全重试，旧回调会被版本号拒绝。
        if (m_StalledTranscriptReviewRequested)
        {
            if (now - m_StalledTranscriptReviewRequestTime < 7f) return;
            ObserveBoundaryRequestDuration(now, false);
            m_ChatSample.CancelTurnBoundaryReview();
            m_StalledTranscriptReviewRequested = false;
            m_StalledReviewFailures++;
            if (m_LogTimings) Debug.LogWarning("[Semantic EOU] 请求看门狗释放失联请求；录音继续，允许重试");
        }
        float unchanged = now - m_LastMeaningfulStreamTextTime;
        float asrAge = Mathf.Max(now - m_LatestStreamPartialTime, now - m_LastStreamProgressTime);
        //只有音频时间戳仍推进，才能把“文字没变”视为稳定证据，而不是网络/ASR停机。
        if (asrAge > 2.5f) return;
        float dropHeld = m_BoundaryAcoustics.DropSince >= 0f
            ? now - m_BoundaryAcoustics.DropSince : 0f;
        bool heldDrop = dropHeld >= m_TurnBoundaryDropHoldSeconds &&
            m_BoundaryAcoustics.DropDb >= m_TurnBoundaryDropDb;
        bool heldQuiet = m_SilenceTimer >= m_TurnBoundaryDropHoldSeconds &&
            m_TurnActivity.CoversQuietTail;
        bool hasThought = m_ChatSample.HasTurnBoundaryThought(m_LatestStreamPartial);
        float required = ResolveStalledReviewDelay(heldDrop || heldQuiet, hasThought,
            m_StalledTranscriptReviewSeconds, m_FastBoundaryTextSeconds);
        required = ResolveReviewDelayWithCoveredQuiet(required, m_SilenceTimer,
            heldQuiet && HasFreshStreamingCoverage &&
            !m_TurnActivity.LevelActive && !m_TurnActivity.MelodyActive &&
            !m_TurnActivity.TextActivityActive);
        bool newDrop = heldDrop && m_BoundaryAcoustics.DropSince > m_LastReviewedDropSince;
        if (unchanged < required || (now < m_NextStalledTranscriptReviewTime && !newDrop))
            return;

        float closeQuietSeconds = m_EnableSingingMode && (m_LikelySinging || m_MelodicEouProtectionActive)
            ? m_SingingEouSilence : m_RecordingTimeLimit;
        bool coveredQuiet = heldQuiet && HasFreshStreamingCoverage && m_TurnActivity.TailAvailable &&
            !m_TurnActivity.LevelActive && !m_TurnActivity.MelodyActive && !m_TurnActivity.TextActivityActive;
        string admissionReason;
        bool allowRemoteRequest = ShouldStartRemoteBoundaryReview(coveredQuiet,
            closeQuietSeconds - m_SilenceTimer, m_ObservedBoundarySeconds,
            now - m_LastBoundaryRemoteAttemptAt, out admissionReason);

        string trigger = heldDrop ? "level-drop" : heldQuiet ? "quiet" :
            hasThought ? "prepared-thought" : "stable-text";
        if (m_BoundaryCandidateTime < 0f) m_BoundaryCandidateTime = now;
        m_StalledRequiredUnchangedSeconds = required;
        m_StalledReviewActivityRevision = m_TurnActivity.ActivityRevision;
        m_StalledReviewTextRevision = m_TurnActivity.Revision - m_TurnActivity.ActivityRevision;
        m_StalledReviewSoundAt = m_TurnActivity.LatestSoundAt;
        m_StalledReviewRemoteAllowed = allowRemoteRequest;
        m_BoundaryRemoteTimingArmed = false;
        m_BoundaryRequestTimingRecorded = false;
        //先设置状态，以兼容 provider 同步失败回调；失败不能被调用后的赋值重新锁住。
        m_StalledTranscriptReviewRequested = true;
        m_StalledTranscriptReviewRequestTime = now;
        m_NextStalledTranscriptReviewTime = now + Mathf.Max(2f, m_StalledTranscriptReviewCooldown);
        if (heldDrop) m_LastReviewedDropSince = m_BoundaryAcoustics.DropSince;
        PublishPreparedTurnBoundaryEvidence();
        bool requested = m_ChatSample.RequestStalledTurnReview(
            m_LatestStreamPartial,
            unchanged,
            Mathf.Max(m_LastMeaningfulStreamTextAudioMs, m_LatestStreamPartialAudioMs),
            currentRms,
            eouThreshold,
            m_AmbientRmsFloor,
            m_AmbientNoiseCalibrated,
            m_LatestStreamingSingingProbability,
            m_CurrentSingingProbability,
            m_RecentStreamingSingingProbability,
            m_StreamingSingingProbabilityTrend,
            m_LastStreamingPitchStability,
            m_StalledContinueDecisionCount,
            m_LastStalledDecisionStatus,
            m_LastStalledDecisionConfidence,
            m_BoundaryAcoustics.DropDb,
            dropHeld,
            asrAge,
            Mathf.Max(0, m_LatestStreamPartialAudioMs - m_LastMeaningfulStreamTextAudioMs),
            trigger,
            m_StalledReviewFailures,
            $"近期有效活动：低电平参考={eouThreshold:F4}，噪声上沿={EffectiveNoiseUpper:F4}，" +
            $"最近电平中值={m_TurnActivity.RecentRms:F4}；有效输入未更新={m_SilenceTimer:F2}s；" +
            $"最近0.8秒声学字段可用={m_TurnActivity.TailAvailable}，回包年龄={m_TurnActivity.TailAge(now):F2}s，" +
            $"当前仍有高于噪声的周期声证据={m_TurnActivity.MelodyActive}。" +
            $"近期人声证据={m_TurnActivity.TextActivityActive}；最近有效声音结束距今={m_SilenceTimer:F2}s；" +
            $"之前有{m_BoundaryRenewedSoundCount}次接话判断期间继续发声，未切开录音。" +
            "音量下降可能只是长音/换气，不等于整段结束；如需要主动打断请明确选择 interrupt_user。" +
            "这只是输入活动证据，不是是否应接话的命令。旧歌词和整窗歌唱概率不证明此刻仍在唱。",
            allowRemoteRequest);
        // The provider may synchronously consume an already prepared decision.
        // Do not count that path as a backend request or train its latency estimate.
        if (requested && allowRemoteRequest && m_StalledTranscriptReviewRequested)
        {
            m_LastBoundaryRemoteAttemptAt = now;
            m_BoundaryRemoteTimingArmed = true;
        }
        if (m_LogTimings || !allowRemoteRequest)
            Debug.Log($"[Semantic EOU/Admission] capture={CurrentCaptureSerial} remote={m_BoundaryRemoteTimingArmed} " +
                $"reason={admissionReason} remainingQuiet={closeQuietSeconds - m_SilenceTimer:F3}s " +
                $"observed={m_ObservedBoundarySeconds:F3}s synchronousCompletion={requested && !m_StalledTranscriptReviewRequested}");
        if (!requested)
        {
            m_StalledTranscriptReviewRequested = false;
            m_NextStalledTranscriptReviewTime = now + 0.8f;
            return;
        }
        if (m_LogTimings)
            Debug.Log($"[Semantic EOU] 文字稳定={unchanged:F2}s trigger={trigger} " +
                      $"rms={currentRms:F4} eou={eouThreshold:F4} drop={m_BoundaryAcoustics.DropDb:F1}dB " +
                      $"held={dropHeld:F2}s asrAge={asrAge:F2}s；" +
                      $"latest/mean/peak={m_LatestStreamingSingingProbability:F2}/" +
                      $"{m_RecentStreamingSingingProbability:F2}/" +
                      $"{m_CurrentSingingProbability:F2} " +
                      $"priorContinue={m_StalledContinueDecisionCount}；" +
                      "由LLM选择继续倾听/主动接话/询问/确认结束");
    }

    private static float ResolveStalledReviewDelay(bool acousticCandidate, bool preparedThought,
        float fallbackSeconds, float fastSeconds)
    {
        return acousticCandidate ? Mathf.Max(0.4f, fastSeconds) :
            preparedThought ? Mathf.Min(Mathf.Max(0.4f, fallbackSeconds), 1.2f) :
            Mathf.Max(1f, fallbackSeconds);
    }

    private static bool ShouldStartRemoteBoundaryReview(bool coveredQuiet, float remainingQuietSeconds,
        float observedSeconds, float secondsSinceLastAttempt, out string reason)
    {
        // An active/uncertain tail or an abnormally stalled stream still needs the
        // role's decision. This admission rule never closes a capture itself.
        if (!coveredQuiet) { reason = "not-covered-quiet"; return true; }
        if (remainingQuietSeconds <= 0f) { reason = "capture-close-due"; return false; }
        if (observedSeconds <= 0f) { reason = "initial-measurement"; return true; }
        if (observedSeconds + .10f < remainingQuietSeconds) { reason = "fits-quiet-budget"; return true; }
        // Periodic exploration can discover a faster/recovered backend. Ordinary
        // quiet turns between probes continue to use the unchanged physical gate.
        if (secondsSinceLastAttempt >= 30f) { reason = "latency-refresh-probe"; return true; }
        reason = "slower-than-remaining-quiet"; return false;
    }

    private void ObserveBoundaryRequestDuration(float now, bool completed)
    {
        if (!m_StalledReviewRemoteAllowed || !m_BoundaryRemoteTimingArmed || m_BoundaryRequestTimingRecorded ||
            !m_StalledTranscriptReviewRequested || m_StalledTranscriptReviewRequestTime < 0f) return;
        float elapsed = now - m_StalledTranscriptReviewRequestTime;
        m_BoundaryRequestTimingRecorded = true;
        if (elapsed <= 0f || float.IsNaN(elapsed) || float.IsInfinity(elapsed)) return;
        elapsed = Mathf.Min(7f, elapsed);
        m_ObservedBoundarySeconds = !completed || m_ObservedBoundarySeconds <= 0f
            ? Mathf.Max(m_ObservedBoundarySeconds, elapsed)
            : Mathf.Lerp(m_ObservedBoundarySeconds, elapsed, .25f);
        Debug.Log($"[Semantic EOU/Latency] capture={CurrentCaptureSerial} seconds={elapsed:F3} " +
            $"sample={(completed ? "response" : "cancelled-lower-bound")} estimate={m_ObservedBoundarySeconds:F3}");
    }

    private static float ResolveReviewDelayWithCoveredQuiet(float textDelay, float quietSeconds,
        bool coveredQuiet)
    {
        // Still ask the LLM about the latest complete semantics. An ASR spelling/filler
        // correction doesn't start a second silence clock; new words invalidate its answer.
        return coveredQuiet && quietSeconds >= textDelay ? 0f : textDelay;
    }

    private void ObserveListeningBoundaryEvidence(float eouThreshold)
    {
        float now = Time.realtimeSinceStartup;
        // The outage fallback needs continuous noise-floor-level quiet, not merely < 0.01.
        m_BoundaryAcoustics.Observe(m_SmoothedRms, now,
            Mathf.Min(eouThreshold, EffectiveNoiseUpper * 1.05f), m_TurnBoundaryDropDb);
        if (!m_LogTimings || now - m_LastBoundaryEvidenceLogTime < 2f) return;
        m_LastBoundaryEvidenceLogTime = now;
        float stable = m_LastMeaningfulStreamTextTime >= 0f ? now - m_LastMeaningfulStreamTextTime : -1f;
        Debug.Log($"[Listening/Evidence] audio={m_LatestStreamPartialAudioMs}ms " +
            $"rms={m_SmoothedRms:F4} ref={m_BoundaryAcoustics.ReferenceRms:F4} " +
            $"recent={m_BoundaryAcoustics.RecentRms:F4} drop={m_BoundaryAcoustics.DropDb:F1}dB " +
            $"dropHeld={(m_BoundaryAcoustics.DropSince >= 0f ? now - m_BoundaryAcoustics.DropSince : 0f):F2}s " +
            $"eou={eouThreshold:F4} textStable={stable:F2}s " +
            $"effectiveQuiet={m_SilenceTimer:F2}s levelActive={m_TurnActivity.LevelActive} " +
            $"recentMelody={m_TurnActivity.MelodyActive} softSpeech={m_TurnActivity.TextActivityActive} pendingText={m_TurnActivity.PendingText} " +
            $"noiseUpper={EffectiveNoiseUpper:F4} tailAvailable={m_TurnActivity.TailAvailable} " +
            $"asrProgressAge={(m_LastStreamProgressTime >= 0f ? now - m_LastStreamProgressTime : -1f):F2}s");
    }

    private void PublishClosedSpeechEvidence()
    {
        bool closed = m_RecordingCloseReason != "manual-disable" &&
            m_RecordingCloseReason != "llm-interrupt_user" && m_RecordingCloseReason != "llm-ask_user";
        bool speech = !m_LikelySinging && !m_MelodicEouProtectionActive &&
            m_PendingSemanticEouObservedMode != "singing" &&
            m_PendingSemanticEouSource != "background" && m_PendingSemanticEouSource != "ai";
        if (!m_ClosedSpeechTracker.TrySeal(CurrentCaptureSerial, Time.realtimeSinceStartup,
                Mathf.RoundToInt(m_StreamSubmittedSeconds * 1000f), closed,
                HasFreshStreamingCoverage && m_TurnActivity.CoversQuietTail,
                m_TurnActivity.PendingText, speech, m_RecordingCloseReason, out var evidence)) return;
        m_NeuralVadClient?.RetainClosedSpeechEvidence(evidence);
        Debug.Log($"[Speech/ClosedEvidence] capture={evidence.CaptureSessionSerial} revision={evidence.Revision} " +
            $"audioMs={evidence.AudioMs}/{evidence.SubmittedAudioMs} stableSeconds={evidence.StableForSeconds:F2} " +
            $"closedAt={evidence.ClosedAt:F3} reason={evidence.CloseReason} textChars={evidence.Text.Length}");
        m_ChatSample?.AcceptClosedSpeechEvidence(evidence.Copy());
    }

    private void PublishListeningEndEvidence()
    {
        if (m_ChatSample == null) return;
        float now = Time.realtimeSinceStartup;
        float anchor = -1f;
        string source = "unknown-no-acoustic-endpoint";
        if (m_TurnActivity.QuietSeconds(now) >= m_TurnBoundaryDropHoldSeconds)
        {
            anchor = m_TurnActivity.QuietSince;
            source = "effective-activity-estimate";
        }
        else if (m_BoundaryAcoustics.DropSince >= 0f &&
            now - m_BoundaryAcoustics.DropSince >= m_TurnBoundaryDropHoldSeconds &&
            m_BoundaryAcoustics.DropDb >= m_TurnBoundaryDropDb)
        {
            anchor = m_BoundaryAcoustics.DropSince;
            source = "relative-level-drop-estimate";
        }
        else if (m_BoundaryAcoustics.QuietSince >= 0f)
        {
            anchor = m_BoundaryAcoustics.QuietSince;
            source = "acoustic-quiet-estimate";
        }
        m_ChatSample.RecordListeningEndEvidence(anchor, source,
            m_LastMeaningfulStreamTextTime, m_BoundaryCandidateTime, m_RecordingCloseReason);
    }

    private void EndPendingSemanticEou(string outcome)
    {
        m_PendingSemanticEouTrace?.Mark("eou.consumer_end", new Newtonsoft.Json.Linq.JObject {
            ["outcome"] = outcome, ["action"] = m_PendingSemanticEouStatus });
        m_PendingSemanticEouTrace = null;
        m_PendingSemanticEou = false;
    }

    private void HandleStalledUserTurnDecision(
        string status,
        float confidence,
        string observedMode,
        float modeConfidence,
        string sourceLikelihood,
        string sourceTranscript,
        int audioMs)
    {
        ObserveBoundaryRequestDuration(Time.realtimeSinceStartup, true);
        m_StalledTranscriptReviewRequested = false;
        m_StalledTranscriptReviewRequestTime = -999f;
        var boundaryTrace = m_ChatSample?.TurnBoundaryTrace;
        if (!m_IsRecording)
        {
            boundaryTrace?.Mark("eou.consumer_end", new Newtonsoft.Json.Linq.JObject { ["outcome"] = "recording_closed" });
            return;
        }
        string normalizedStatus = (status ?? "").Trim().ToLowerInvariant();
        bool sameText = string.Equals(NormalizeSemanticTranscript(sourceTranscript),
            NormalizeSemanticTranscript(m_LatestStreamPartial), StringComparison.Ordinal);
        bool sameActivity = m_StalledReviewActivityRevision == m_TurnActivity.ActivityRevision;
        bool sameTextRevision = m_StalledReviewTextRevision ==
            m_TurnActivity.Revision - m_TurnActivity.ActivityRevision;
        bool fresh = sameText && sameTextRevision && sameActivity && !m_TurnActivity.PendingText;
        bool newSound = HasSoundAdvanced(m_StalledReviewSoundAt, m_TurnActivity.LatestSoundAt);
        if (newSound && normalizedStatus != "interrupt_user")
        {
            m_BoundaryRenewedSoundCount++;
            fresh = false;
        }
        var checkedEvidence = BoundaryDecisionObservation.Fields(
            normalizedStatus, confidence, sameText, sameActivity, m_TurnActivity.PendingText,
            newSound, HasFreshStreamingCoverage, m_StalledReviewActivityRevision, m_TurnActivity.ActivityRevision,
            audioMs, m_LatestStreamPartialAudioMs);
        checkedEvidence["sameTextRevision"] = sameTextRevision;
        checkedEvidence["sourceTextRevision"] = m_StalledReviewTextRevision;
        checkedEvidence["currentTextRevision"] = m_TurnActivity.Revision - m_TurnActivity.ActivityRevision;
        boundaryTrace?.Mark("eou.consumer_checked", checkedEvidence);
        if (!fresh || !TurnBoundaryDecision.IsAction(normalizedStatus))
        {
            boundaryTrace?.Mark("eou.consumer_end", new Newtonsoft.Json.Linq.JObject {
                ["outcome"] = fresh ? "invalid_decision" : "input_changed",
                ["action"] = normalizedStatus });
            EndPendingSemanticEou("replaced_by_rejected_decision");
            if (fresh) m_StalledReviewFailures++;
            m_NextStalledTranscriptReviewTime = Time.realtimeSinceStartup + 0.8f;
            if (m_LogTimings) Debug.Log("[Semantic EOU] 释放结果并重试：" +
                (fresh ? "无效协议/请求失败" : "输入已变化或出现新的起音"));
            return;
        }
        m_LastStalledDecisionStatus = normalizedStatus;
        m_LastStalledDecisionConfidence = Mathf.Clamp01(confidence);
        if (normalizedStatus == "continue")
        {
            m_StalledContinueDecisionCount++;
            boundaryTrace?.Mark("eou.consumer_end", new Newtonsoft.Json.Linq.JObject {
                ["outcome"] = "continue_listening", ["action"] = normalizedStatus });
            EndPendingSemanticEou("continue_listening");
            m_NextStalledTranscriptReviewTime =
                Time.realtimeSinceStartup + Mathf.Max(2f, m_StalledTranscriptReviewCooldown);
            return;
        }
        if (normalizedStatus != "complete" && normalizedStatus != "ask_user" && normalizedStatus != "interrupt_user" &&
            normalizedStatus != "take_turn") return;

        EndPendingSemanticEou("superseded");
        m_PendingSemanticEouTrace = boundaryTrace;
        m_PendingSemanticEouTrace?.Mark("eou.consumer_pending");
        m_PendingSemanticEou = true;
        m_PendingSemanticDecisionAt = Time.realtimeSinceStartup;
        m_LoggedSemanticCoverageWait = false;
        m_PendingSemanticEouStatus = normalizedStatus;
        m_PendingSemanticEouConfidence = Mathf.Clamp01(confidence);
        m_PendingSemanticEouObservedMode = observedMode ?? "uncertain";
        m_PendingSemanticEouModeConfidence = Mathf.Clamp01(modeConfidence);
        m_PendingSemanticEouSource = sourceLikelihood ?? "uncertain";
        m_PendingSemanticEouTextKey = NormalizeSemanticTranscript(sourceTranscript);
        m_PendingSemanticEouTrace?.Mark("eou.semantic_eot", new Newtonsoft.Json.Linq.JObject {
            ["status"] = m_PendingSemanticEouStatus,
            ["confidence"] = m_PendingSemanticEouConfidence,
            ["mode"] = m_PendingSemanticEouObservedMode,
            ["source"] = m_PendingSemanticEouSource,
            ["textKeyCharacters"] = m_PendingSemanticEouTextKey == null ? 0 : m_PendingSemanticEouTextKey.Length,
            ["partialMaxLagMs"] = m_StreamPartialMaxLagMs,
            ["partialMaxAgeSeconds"] = m_StreamPartialMaxAgeSeconds,
            ["coverageWaitTimeoutSeconds"] = m_CoverageWaitTimeoutSeconds,
            ["scope"] = "semantic close candidate; recorder remains open until final evidence gate"
        });
    }

    private bool TryApplyPendingSemanticEou()
    {
        if (!m_IsRecording || !m_PendingSemanticEou) return false;
        if (m_PendingSemanticEouStatus != "interrupt_user" &&
            HasSoundAdvanced(m_StalledReviewSoundAt, m_TurnActivity.LatestSoundAt))
        {
            m_BoundaryRenewedSoundCount++;
            EndPendingSemanticEou("new_sound");
            m_NextStalledTranscriptReviewTime = Time.realtimeSinceStartup;
            Debug.Log("[Semantic EOU] 判断后声音继续，即使文字没变也保留同一录音；交给角色更新时机判断");
            return false;
        }
        float unchanged = Time.realtimeSinceStartup - m_LastMeaningfulStreamTextTime;
        bool accepted = ShouldAcceptStalledTurnDecision(
            m_PendingSemanticEouStatus,
            m_PendingSemanticEouConfidence,
            m_StalledTurnDecisionMinConfidence,
            m_PendingSemanticEouTextKey,
            NormalizeSemanticTranscript(m_LatestStreamPartial),
            unchanged,
            m_StalledRequiredUnchangedSeconds) &&
            m_StalledReviewActivityRevision == m_TurnActivity.ActivityRevision &&
            m_StalledReviewTextRevision == m_TurnActivity.Revision - m_TurnActivity.ActivityRevision &&
            !m_TurnActivity.PendingText;
        bool coverageDegraded = false;
        if (accepted && !HasFreshStreamingCoverage)
        {
            // Retain a valid role choice while one ASR packet catches up, rather than
            // asking the same question again. New input/outage still invalidates it.
            float waiting = Time.realtimeSinceStartup - m_PendingSemanticDecisionAt;
            float coverageLimit = Mathf.Max(0.5f, m_CoverageWaitTimeoutSeconds);
            bool quietTailEvidence = HasDegradedQuietTailEvidence(Time.realtimeSinceStartup);
            float degradedGrace = Mathf.Max(0.3f, m_DegradedCoverageGraceSeconds);
            if (quietTailEvidence && waiting >= degradedGrace)
            {
                coverageDegraded = true;
                m_PendingSemanticEouTrace?.Mark("eou.coverage_degraded", new Newtonsoft.Json.Linq.JObject {
                    ["waitingSeconds"] = waiting, ["graceSeconds"] = degradedGrace,
                    ["sourceAudioMs"] = m_LastMeaningfulStreamTextAudioMs,
                    ["currentAudioMs"] = m_LatestStreamPartialAudioMs,
                    ["partialMaxLagMs"] = m_StreamPartialMaxLagMs,
                    ["scope"] = "quiet tail and stable decision accepted; final ASR still gates formal dispatch"
                });
            }
            else if (m_PendingSemanticDecisionAt >= 0f && waiting <= coverageLimit)
            {
                if (!m_LoggedSemanticCoverageWait && m_LogTimings)
                    Debug.Log($"[Semantic EOU] 保留 {m_PendingSemanticEouStatus}，等待尾部ASR覆盖；不重复请求LLM");
                m_PendingSemanticEouTrace?.Mark("eou.coverage_wait", new Newtonsoft.Json.Linq.JObject {
                    ["waitingSeconds"] = waiting, ["limitSeconds"] = coverageLimit,
                    ["sourceAudioMs"] = m_LastMeaningfulStreamTextAudioMs,
                    ["currentAudioMs"] = m_LatestStreamPartialAudioMs,
                    ["partialMaxLagMs"] = m_StreamPartialMaxLagMs,
                    ["partialMaxAgeSeconds"] = m_StreamPartialMaxAgeSeconds });
                m_LoggedSemanticCoverageWait = true;
                return false;
            }
            m_PendingSemanticEouTrace?.Mark("eou.coverage_timeout", new Newtonsoft.Json.Linq.JObject {
                ["waitingSeconds"] = waiting, ["limitSeconds"] = coverageLimit,
                ["sourceAudioMs"] = m_LastMeaningfulStreamTextAudioMs,
                ["currentAudioMs"] = m_LatestStreamPartialAudioMs,
                ["partialMaxLagMs"] = m_StreamPartialMaxLagMs,
                ["partialMaxAgeSeconds"] = m_StreamPartialMaxAgeSeconds,
                ["streamState"] = m_StreamingTransportState,
                ["streamReason"] = m_StreamingTransportReason
            });
            accepted = false;
        }
        if (!accepted)
        {
            //无论是低置信度、过期文字还是用户恢复发声，都必须释放 pending。
            //旧实现只清理文字变动，导致 ask_user/0.60 永久卡住并阻止一切后续复核。
            m_PendingSemanticEouTrace?.Mark("eou.apply_rejected", BoundaryDecisionObservation.Fields(
                m_PendingSemanticEouStatus, m_PendingSemanticEouConfidence,
                m_PendingSemanticEouTextKey == NormalizeSemanticTranscript(m_LatestStreamPartial),
                m_StalledReviewActivityRevision == m_TurnActivity.ActivityRevision, m_TurnActivity.PendingText,
                false, HasFreshStreamingCoverage, m_StalledReviewActivityRevision, m_TurnActivity.ActivityRevision,
                m_LastMeaningfulStreamTextAudioMs, m_LatestStreamPartialAudioMs,
                unchanged, m_StalledRequiredUnchangedSeconds, m_StalledTurnDecisionMinConfidence));
            EndPendingSemanticEou("acceptance_conditions_not_met");
            m_NextStalledTranscriptReviewTime = Time.realtimeSinceStartup + 0.8f;
            if (m_LogTimings) Debug.Log($"[Semantic EOU] 未采纳 {m_PendingSemanticEouStatus}/" +
                $"{m_PendingSemanticEouConfidence:F2}，pending已释放，稍后重新听取角色判断");
            return false;
        }

            m_PendingSemanticEouTrace?.Mark("eou.final_evidence_ready", new Newtonsoft.Json.Linq.JObject {
            ["status"] = m_PendingSemanticEouStatus,
            ["confidence"] = m_PendingSemanticEouConfidence,
            ["coverage"] = HasFreshStreamingCoverage,
            ["coverageDegraded"] = coverageDegraded,
            ["sourceAudioMs"] = m_LastMeaningfulStreamTextAudioMs,
            ["currentAudioMs"] = m_LatestStreamPartialAudioMs,
            ["partialMaxLagMs"] = m_StreamPartialMaxLagMs,
            ["partialMaxAgeSeconds"] = m_StreamPartialMaxAgeSeconds,
            ["scope"] = coverageDegraded
                ? "semantic decision accepted on quiet-tail fallback; final ASR still gates formal dispatch"
                : "semantic decision accepted and final ASR evidence gate passed"
        });

        if (m_LogTimings)
            Debug.Log($"[Semantic EOU] 采纳LLM轮次判断 {m_PendingSemanticEouStatus}/" +
                      $"{m_PendingSemanticEouConfidence:F2} source={m_PendingSemanticEouSource}；" +
                      $"文字稳定={unchanged:F1}s，结束当前采集");
        if (m_ChatSample != null)
            m_ChatSample.CommitStalledTurnDecisionNote(
                m_PendingSemanticEouStatus,
                m_PendingSemanticEouConfidence,
                m_PendingSemanticEouObservedMode,
                m_PendingSemanticEouModeConfidence,
                m_PendingSemanticEouSource);
        EndPendingSemanticEou("accepted_close");
        m_RecordingCloseReason = coverageDegraded
            ? "llm-" + m_PendingSemanticEouStatus + "-coverage-degraded"
            : "llm-" + m_PendingSemanticEouStatus;
        StopRecording(m_TentativeUsedFullAsr &&
            (m_TentativePreviewInFlight || m_TentativeFullAsrResultReady));
        return true;
    }

    private static bool ShouldAcceptStalledTurnDecision(
        string status,
        float confidence,
        float minimumConfidence,
        string decisionTextKey,
        string currentTextKey,
        float unchangedSeconds,
        float requiredUnchangedSeconds)
    {
        string normalizedStatus = (status ?? "").Trim().ToLowerInvariant();
        return TurnBoundaryDecision.CanClose(normalizedStatus, confidence, minimumConfidence) &&
            !string.IsNullOrEmpty(decisionTextKey) &&
            string.Equals(decisionTextKey, currentTextKey, StringComparison.Ordinal) &&
            unchangedSeconds >= requiredUnchangedSeconds;
    }

    private static bool HasSoundAdvanced(float reviewedSoundAt, float latestSoundAt) =>
        reviewedSoundAt >= 0f && latestSoundAt - reviewedSoundAt >= 0.12f;

    private static bool HasMeaningfulTranscriptChange(string previousKey, string currentKey)
    {
        if (string.IsNullOrEmpty(currentKey)) return false;
        if (string.IsNullOrEmpty(previousKey)) return true;
        if (string.Equals(previousKey, currentKey, StringComparison.Ordinal)) return false;
        int delta = Mathf.Abs(previousKey.Length - currentKey.Length);
        //长句末尾的 1～4 个字符是流式 ASR 最常见的回滚区。实测外放残留会在
        //同一句后反复附加/撤回 “我 / The / I”，旧逻辑把它们当新语义并无限刷新
        //EOU 计时。这里只忽略“整句互为前缀”的极短边界改写；真正的句中改写、
        //更长增长和全新短句仍照常进入 LLM 判断。
        if (delta <= 4 &&
            (previousKey.StartsWith(currentKey, StringComparison.Ordinal) ||
             currentKey.StartsWith(previousKey, StringComparison.Ordinal)))
            return false;
        return true;
    }

    private static string NormalizeSemanticTranscript(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = char.ToLowerInvariant(value[i]);
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            builder.Append(c);
        }
        return builder.ToString();
    }

    private void ResetStalledTurnState()
    {
        ObserveBoundaryRequestDuration(Time.realtimeSinceStartup, false);
        m_TurnActivity.Reset(Time.realtimeSinceStartup);
        m_BoundaryAcoustics.Reset();
        m_BoundaryCandidateTime = -1f;
        m_LastReviewedDropSince = -1f;
        m_LastBoundaryEvidenceLogTime = -1f;
        m_StalledRequiredUnchangedSeconds = 0f;
        m_StalledReviewActivityRevision = 0;
        m_StalledReviewTextRevision = 0;
        m_StalledReviewRemoteAllowed = false;
        m_BoundaryRemoteTimingArmed = false;
        m_BoundaryRequestTimingRecorded = false;
        m_StalledReviewSoundAt = -1f;
        m_BoundaryRenewedSoundCount = 0;
        m_StalledReviewFailures = 0;
        m_RecordingCloseReason = "physical-eou";
        m_LastMeaningfulStreamText = "";
        m_LastMeaningfulStreamTextKey = "";
        m_LastMeaningfulStreamTextTime = -999f;
        m_LastMeaningfulStreamTextAudioMs = 0;
        m_NextStalledTranscriptReviewTime = 0f;
        m_StalledTranscriptReviewRequested = false;
        m_StalledTranscriptReviewRequestTime = -999f;
        EndPendingSemanticEou("listening_reset");
        m_PendingSemanticEouStatus = "";
        m_PendingSemanticEouTextKey = "";
        m_PendingSemanticEouConfidence = 0f;
        m_PendingSemanticEouObservedMode = "";
        m_PendingSemanticEouModeConfidence = 0f;
        m_PendingSemanticEouSource = "";
        m_PendingSemanticDecisionAt = -1f;
        m_LoggedSemanticCoverageWait = false;
        m_LastStreamingPitchStability = 0f;
        m_LatestStreamingSingingProbability = 0f;
        m_RecentStreamingSingingProbability = 0f;
        m_StreamingSingingProbabilityTrend = 0f;
        m_PreviousStreamingSingingProbability = 0f;
        m_HasStreamingSingingProbability = false;
        m_StalledContinueDecisionCount = 0;
        m_LastStalledDecisionStatus = "";
        m_LastStalledDecisionConfidence = 0f;
    }

    private static bool ShouldApplyDirectAcousticSingingLatch(
        bool hasSemanticCoordinator,
        bool singingModeEnabled,
        bool frameClassifiedSinging,
        float singingProbability,
        float threshold)
    {
        return !hasSemanticCoordinator && singingModeEnabled &&
            (frameClassifiedSinging || singingProbability >= threshold);
    }

    private void UpdateMelodicEouProtection(
        SenseVoiceSpeechToText.StreamingTranscript transcript)
    {
        if (!m_EnableSingingMode || transcript.AudioMs <= m_LastMelodicEouEvidenceAudioMs)
            return;
        m_LastMelodicEouEvidenceAudioMs = transcript.AudioMs;

        bool melodicFrame = transcript.IsSinging ||
            (transcript.SingingProbability >= 0.58f && transcript.PitchStability >= 0.40f) ||
            (transcript.SingingProbability >= 0.54f && transcript.PitchStability >= 0.58f);
        if (melodicFrame)
        {
            m_MelodicEouEvidenceFrames++;
            m_MelodicEouLowFrames = 0;
            if (!m_MelodicEouProtectionActive && m_MelodicEouEvidenceFrames >= 3)
            {
                m_MelodicEouProtectionActive = true;
                if ((!RoleChoosesBoundary || m_TurnActivity.MelodyActive) &&
                    (m_TentativeFired || m_TentativePreviewInFlight || m_TentativeCompletePending))
                    InvalidateTentativeEou("sustained-melody-eou-protection");
                if (m_LogTimings)
                    Debug.Log($"[Singing/EOU] 连续旋律证据已启用停唱保护 " +
                              $"p={transcript.SingingProbability:F2} " +
                              $"pitch={transcript.PitchStability:F2} " +
                              $"audio={transcript.AudioMs}ms；最终模态仍交给LLM复核");
            }
            return;
        }

        m_MelodicEouEvidenceFrames = 0;
        if (m_MelodicEouProtectionActive &&
            transcript.SingingProbability < 0.32f)
        {
            m_MelodicEouLowFrames++;
            if (m_MelodicEouLowFrames >= 3)
            {
                m_MelodicEouProtectionActive = false;
                m_MelodicEouLowFrames = 0;
                if (m_LogTimings)
                    Debug.Log("[Singing/EOU] 连续低旋律证据解除停唱保护；恢复普通EOU");
            }
        }
        else
        {
            m_MelodicEouLowFrames = 0;
        }
    }

    private void ResetMelodicEouProtection()
    {
        m_MelodicEouProtectionActive = false;
        m_MelodicEouEvidenceFrames = 0;
        m_MelodicEouLowFrames = 0;
        m_LastMelodicEouEvidenceAudioMs = -1;
    }

    private void PumpStreamingAudio(int currentPos, bool force)
    {
        if (m_StreamLastSentPos < 0 || m_NeuralVadClient == null || m_RecordedClip == null ||
            m_StreamingPushStopped) return;
        if (!force && Time.realtimeSinceStartup < m_NextStreamAudioPushTime) return;
        if (currentPos == m_StreamLastSentPos) return;

        float[] samples = CopySamplesFromBuffer(m_StreamLastSentPos, currentPos);
        m_StreamLastSentPos = currentPos;
        m_NextStreamAudioPushTime = Time.realtimeSinceStartup + Mathf.Max(0.05f, m_StreamAudioFrameSeconds);
        if (samples == null || samples.Length == 0) return;
        // Stream time excludes playback-quarantined chunks; the final PCM retains them.
        bool accepted = m_NeuralVadClient.TryPushStreamingSamples(
            samples, m_RecordedClip.channels, m_RecordedClip.frequency);
        if (!accepted)
        {
            // The preview queue is bounded.  Once it rejects a chunk, stop
            // claiming coverage and retain only the authoritative PCM capture.
            m_StreamingPushStopped = true;
            m_StreamingTerminalAt = Time.realtimeSinceStartup;
            m_StreamingTransportState = "error";
            m_StreamingTransportReason = "queue-backpressure";
            m_ChatSample?.TraceCaptureStreamChanged(
                CurrentCaptureSerial, "error", m_StreamingTransportReason,
                m_StreamingTransportGeneration, m_StreamSubmittedSeconds,
                m_RecordingCapturedAt >= 0f
                    ? Time.realtimeSinceStartup - m_RecordingCapturedAt : -1f);
            return;
        }
        m_StreamSubmittedSeconds += samples.Length /
            (float)(Mathf.Max(1, m_RecordedClip.channels) * Mathf.Max(1, m_RecordedClip.frequency));
        m_LastStreamAudioSubmittedAt = Time.realtimeSinceStartup;
    }

    /// <summary>从环形 AudioClip 复制交错 samples，不创建临时 AudioClip，避免 10Hz GC 抖动。</summary>
    private float[] CopySamplesFromBuffer(int startPos, int endPos)
    {
        if (m_RecordedClip == null) return null;
        int total = m_RecordedClip.samples;
        if (total <= 0 || startPos < 0 || startPos >= total || endPos < 0 || endPos >= total)
            return null;
        int frameCount = RingSampleDistance(startPos, endPos, total);
        if (frameCount <= 0) return null;

        int channels = m_RecordedClip.channels;
        float[] data = new float[frameCount * channels];
        if (endPos >= startPos)
        {
            m_RecordedClip.GetData(data, startPos);
            return data;
        }

        int firstFrames = total - startPos;
        float[] first = new float[firstFrames * channels];
        m_RecordedClip.GetData(first, startPos);
        System.Array.Copy(first, 0, data, 0, first.Length);
        if (endPos > 0)
        {
            float[] second = new float[endPos * channels];
            m_RecordedClip.GetData(second, 0);
            System.Array.Copy(second, 0, data, first.Length, second.Length);
        }
        return data;
    }

    private static int RingSampleDistance(int startPos, int endPos, int total)
    {
        return endPos >= startPos ? endPos - startPos : total - startPos + endPos;
    }

    private bool TryGetFreshStreamingPartial(int currentPos, out string text)
    {
        text = "";
        if (!m_EnableStreamingRecognition || string.IsNullOrWhiteSpace(m_LatestStreamPartial) ||
            m_LatestStreamPartialTime < 0f || m_RecordingStartPos < 0 || m_RecordedClip == null)
            return false;

        float age = Time.realtimeSinceStartup - m_LatestStreamPartialTime;
        int currentMs = Mathf.RoundToInt(
            RingSampleDistance(m_RecordingStartPos, currentPos, m_RecordedClip.samples) *
            1000f / Mathf.Max(1, m_RecordedClip.frequency));
        int lagMs = Mathf.Max(0, currentMs - m_LatestStreamPartialAudioMs);
        if (age > m_StreamPartialMaxAgeSeconds || lagMs > m_StreamPartialMaxLagMs)
            return false;

        text = m_LatestStreamPartial;
        return true;
    }

    /// <summary>
    /// Returns a stable speech transcript suitable for provisional evidence at
    /// EOU.  It is never formal input; whole-clip final ASR remains authoritative.
    /// Singing, tonal rescue, stale coverage and low-confidence frames are
    /// filtered before even recording this provisional evidence.
    /// </summary>
    private bool TryGetDispatchableStablePartial(int currentPos, out string text)
    {
        text = "";
        if (!m_EnableStablePartialPromotion || !m_EnableStreamingRecognition || m_ChatSample == null ||
            m_RecordedClip == null ||
            m_RecordingStartPos < 0 || string.IsNullOrWhiteSpace(m_LatestStreamStablePartial) ||
            m_LatestStreamStablePartialTime < 0f)
            return false;

        // Any positive singing latch or tonal rescue means the final transcript
        // must carry the acoustic/mixed-turn evidence before routing.
        if (m_LikelySinging || m_MelodicEouProtectionActive ||
            m_RecordingRescuedByTonalOverride || m_CurrentSingingProbability >= 0.48f ||
            m_LatestStreamingSingingProbability >= 0.48f)
            return false;

        string candidate = m_LatestStreamStablePartial.Trim();
        if (candidate.Length < 2) return false;
        float now = Time.realtimeSinceStartup;
        float age = now - m_LatestStreamStablePartialTime;
        int currentMs = Mathf.RoundToInt(
            RingSampleDistance(m_RecordingStartPos, currentPos, m_RecordedClip.samples) *
            1000f / Mathf.Max(1, m_RecordedClip.frequency));
        int lagMs = Mathf.Max(0, currentMs - m_LatestStreamStablePartialAudioMs);
        if (age > m_StreamPartialMaxAgeSeconds || lagMs > m_StreamPartialMaxLagMs)
            return false;

        text = candidate;
        return true;
    }

    /// <summary>
    /// 兜底：保证mic处于监听态。新流程下StopRecording已经会立刻重启mic，
    /// 所以多数情况下这里只是确认。仍保留以应对Microphone被外部停掉的场景。
    /// </summary>
    public void ReStartRecord()
    {
        if (!m_AwakeState && !m_ManualCaptureActive && !m_IsRecording &&
            m_VoiceAWake == null)
        {
            ReleaseMicrophone();
            m_LockState = false;
            return;
        }
        TryEnsureMicrophone(out _);
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

    private static bool CanPrefetchFinalAnalysis(float quiet, float heldDrop,
        bool recentMelody, float sinceLastRequest)
    {
        return !recentMelody && sinceLastRequest >= 1.5f &&
            (quiet >= 0.8f || (quiet >= 0.30f && heldDrop >= 0.35f));
    }

    /// <summary>
    /// 沉默达到m_TentativeEouSilence时调用：
    /// 1) 截当前正在录的clip(从start到now的位置)
    /// 2) 异步发预测ASR——回调里看尾部是否"说完了"再决定提前EOU还是走普通硬收束
    /// </summary>
    private void TryFireTentativeEou()
    {
        if (m_ChatSample == null) return;
        if (m_RecordedClip == null) return;
        if (m_RecordingStartPos < 0) return;  //不在录用户发言

        // Invalidation is deliberately cancellable, but the cancelled HTTP
        // request may still be winding down on the server.  Do not launch a
        // second full analysis for the same capture after a brief breath or
        // VAD revision; the final ASR path already owns the authoritative
        // complete clip.  Streaming partial reuse remains available because
        // it does not enqueue another snapshot upload.
        if (m_TentativeFullPreviewAttempted &&
            (RoleChoosesBoundary || !TryGetFreshStreamingPartial(
                GetMicrophonePosition(m_ActiveMicrophoneName), out _)))
        {
            if (m_LogTentativeEou && !m_TentativeFullPreviewRetrySuppressed)
            {
                Debug.Log("[T-EOU] 跳过同一 capture 的重复完整预览；等待最终 ASR 收口");
                m_TentativeFullPreviewRetrySuppressed = true;
            }
            return;
        }

        int curPos = GetMicrophonePosition(m_ActiveMicrophoneName);
        string streamingText;
        if (!RoleChoosesBoundary && TryGetFreshStreamingPartial(curPos, out streamingText))
        {
            m_TentativeFired = true;
            m_TentativePreviewInFlight = false;
            m_TentativeUsedFullAsr = false;
            m_TentativeFullAsrResultReady = false;
            m_TentativeFullAsrText = "";
        m_TentativeFullAsrRawText = "";
            m_TentativeSeq++;
            int streamingSeq = m_TentativeSeq;
            m_TentativePreviewSentTime = Time.realtimeSinceStartup;
            if (m_LogTentativeEou)
                Debug.Log($"[T-EOU] 复用流式 partial (seq={streamingSeq}, 沉默{m_SilenceTimer:F2}s): \"{streamingText}\"");
            OnPreviewAsrResult(streamingSeq, streamingText);
            return;
        }

        //流式 partial 尚未覆盖到尾部时，回落到原快照预览，避免为了抢几十毫秒而误切句。
        //预测ASR看到的尾部样本和最终ASR看到的尾部样本完全相同。
        CaptureRecordingAudio(curPos, true);
        AudioClip snapshot = BuildAccumulatedRecordingClip(false);
        if (snapshot == null) snapshot = SnapshotFromBuffer(m_RecordingStartPos, curPos);
        if (snapshot == null) return;

        m_TentativeFullPreviewAttempted = true;
        m_TentativeFullAsrActivityRevision = m_TurnActivity.ActivityRevision;
        m_TentativeFullAsrSoundAt = m_TurnActivity.LatestSoundAt;
        m_TentativeFired = true;
        m_TentativePreviewInFlight = true;
        m_TentativeUsedFullAsr = true;
        m_TentativeFullAsrResultReady = false;
        m_TentativeFullAsrText = "";
        m_TentativeFullAsrRawText = "";
        m_TentativeSeq++;
        int seqAtFire = m_TentativeSeq;
        m_TentativePreviewSentTime = Time.realtimeSinceStartup;

        if (m_LogTentativeEou)
            Debug.Log($"[T-EOU] 派发预测ASR (seq={seqAtFire}, 沉默{m_SilenceTimer:F2}s, clip={snapshot.length:F2}s)");

        m_ChatSample.PreviewASR(
            snapshot,
            (text) => OnPreviewAsrResult(seqAtFire, text),
            m_CurrentRecordingAllowsSpeakerLearning,
            rawText =>
            {
                if (seqAtFire == m_TentativeSeq &&
                    (m_IsRecording || (m_AwaitingTentativeFinal && seqAtFire == m_AwaitingTentativeSeq)))
                {
                    m_TentativeFullAsrRawText = rawText ?? "";
                    m_TentativeFullAsrTranscriptKey = NormalizeSemanticTranscript(rawText);
                }
            });
    }

    /// <summary>
    /// 预测ASR回包。可能在以下任一时刻到达：
    /// - 用户仍在沉默中(m_IsRecording=true) → 进入分类器决定确认/继续等
    /// - 用户已重新开口 → seq不匹配，丢弃
    /// - 普通语音硬规则已触发 → seq不匹配 OR m_IsRecording=false，丢弃
    /// </summary>
    private void OnPreviewAsrResult(int seqAtFire, string text)
    {
        // A cancelled old request must not clear a newer request's in-flight flag.
        if (seqAtFire == m_TentativeSeq ||
            (m_AwaitingTentativeFinal && seqAtFire == m_AwaitingTentativeSeq))
            m_TentativePreviewInFlight = false;

        if (m_AwaitingTentativeFinal && seqAtFire == m_AwaitingTentativeSeq)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                m_TentativeFullAsrResultReady = true;
                m_TentativeFullAsrText = text;
                CompleteAwaitingTentativeFinal(text);
            }
            else
            {
                FallbackAwaitingTentativeFinal("preview-empty");
            }
            return;
        }

        //过期判定：用户开口/硬收束兜底已经使Invalidate把seq推进了
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

        if (m_TentativeUsedFullAsr)
        {
            m_TentativeFullAsrResultReady = !string.IsNullOrWhiteSpace(text);
            m_TentativeFullAsrText = text ?? "";
        }

        float roundTrip = Time.realtimeSinceStartup - m_TentativePreviewSentTime;
        if (RoleChoosesBoundary)
        {
            //Prepare the expensive final analysis while the role judges timing. Punctuation
            //is not a second authority overriding its decision. This full result can be reused.
            if (m_LogTentativeEou)
                Debug.Log($"[T-EOU] 完整ASR已预备 RT={roundTrip:F2}s；不使用标点裁决，等待角色判断/有效静默收束");
            return;
        }
        string cls = ClassifyEnding(text);

        if (m_LogTentativeEou)
            Debug.Log($"[T-EOU] 预测回包 (seq={seqAtFire}, RT={roundTrip:F2}s, cls={cls}): \"{text}\"");

        if (cls == "complete")
        {
            float confirmAt = Mathf.Max(m_TentativeEouSilence, m_TentativeEouConfirmSilence);
            if (m_SilenceTimer >= confirmAt)
            {
                ConfirmEouFromPreview(text);
            }
            else
            {
                m_TentativeCompletePending = true;
                m_TentativeCompleteText = text ?? "";
                if (m_LogTentativeEou)
                    Debug.Log($"[T-EOU] 完整候选等待恢复窗口 ({m_SilenceTimer:F2}/{confirmAt:F2}s)");
            }
        }
        //incomplete / ambiguous：保持m_TentativeFired=true，不再发预测；
        //不再尝试 mid-utterance 搭腔（路 A：彻底放弃 mid-utterance backchannel）。
        //等 m_RecordingTimeLimit 硬规则兜底，或用户重新开口走 Invalidate 路径。
        //更长沉默会由 Silence 事件兜底 (路 C)。
    }

    /// <summary>
    /// 提前确认EOU。WebSocket partial 只负责“是否结束”的判断；
    /// 若同轮完整预测ASR已成功，则可直接晋升为最终文本，否则仍由最终 /asr 校正。
    /// </summary>
    private void ConfirmEouFromPreview(string text)
    {
        if (!m_IsRecording) return;

        int curPos = GetMicrophonePosition(m_ActiveMicrophoneName);
        CaptureRecordingAudio(curPos, true);
        PumpStreamingAudio(curPos, true);
        m_RecordingCloseReason = "tentative-eou";
        RetainUnheardCapture(curPos);
        AudioClip finalClip = BuildAccumulatedRecordingClip();
        if (finalClip == null && m_RecordingStartPos >= 0)
            finalClip = SnapshotFromBuffer(m_RecordingStartPos, curPos);
        SealPersonalLearningCapture(finalClip, true);
        //A full preview ASR ran on the same captured content; between its snapshot
        //and confirmation only trusted silence elapsed.  It is already the
        //authoritative full analysis, unlike a WebSocket partial.
        bool reuseFullPreview = m_TentativeUsedFullAsr &&
            m_TentativeFullAsrResultReady &&
            !string.IsNullOrWhiteSpace(m_TentativeFullAsrText);
        bool useFinalAsr = m_EnableStreamingRecognition && finalClip != null &&
            !reuseFullPreview;
        bool allowSpeakerLearning = m_CurrentRecordingAllowsSpeakerLearning;
        m_CurrentRecordingAllowsSpeakerLearning = true;

        HoldAmbientLearningAtHandoff();
        m_IsRecording = false;
        //先取出再清零：下面的 MarkEOU 要用它决定是否抑制快速应声，
        //而清零发生在它之前，直接传字段永远是 false。
        bool rescuedByTonalOverride = m_RecordingRescuedByTonalOverride;
        m_RecordingRescuedByTonalOverride = false;
        if (m_UnheardCapture.Count == 0) EndStreamingRecognition();
        m_RecordingStartPos = -1;

        //★ mic保持continuous loop=true运行，不再End/Start——文本已经在手，clip角色完成使命；
        //  保留ring buffer持续监听，下一轮VAD/barge-in才能立刻检测到用户开口。
        TryEnsureMicrophone(out _);
        m_LockState = false;

        if (m_ChatSample != null)
        {
            m_RecordingCloseReason = "tentative-eou";
            PublishListeningEndEvidence();
            PublishClosedSpeechEvidence();
            m_ChatSample.CancelTurnBoundaryReview();
            //EOU锚点：保持和StopRecording一致的语义，方便ASR/LLM/TTS阶段延迟统计
            m_ChatSample.MarkEOU(rescuedByTonalOverride);
            if (m_LogTimings)
            {
                float saved = m_RecordingTimeLimit - m_TentativeEouSilence;
                Debug.Log($"[Timing] T-EOU 提前确认 — 文本=\"{text}\", 节省≈{saved:F1}s");
            }
            if (useFinalAsr)
            {
                // A stable ordinary-speech partial is retained for speculative
                // listening only.  The complete clip remains authoritative and
                // every formal RoleDecision stays on the final-ASR-first path.
                bool safeSpeechPartial = !m_LikelySinging &&
                    !m_MelodicEouProtectionActive && !m_RecordingRescuedByTonalOverride &&
                    m_CurrentSingingProbability < 0.48f &&
                    m_LatestStreamingSingingProbability < 0.48f &&
                    !string.IsNullOrWhiteSpace(text);
                bool partialHeld = m_EnableStablePartialPromotion && safeSpeechPartial &&
                    m_ChatSample.AcceptStablePartialText(text);
                //partial 可能在最后几个字发生回滚；它只用于内部预判，
                //完整 ASR 回调才会进入正式 RoleDecision/历史。
                m_ChatSample.AcceptClip(finalClip, allowSpeakerLearning);
                if (m_LogTimings && partialHeld)
                    Debug.Log("[T-EOU] 稳定 speech partial 已保留作预判；正式 LLM 等待完整 ASR");
            }
            else
            {
                //关闭流式功能，或完整预测已经覆盖有效内容时，避免第二次识别。
                m_NeuralVadClient?.PublishReusedFinalTranscript(CurrentCaptureSerial,
                    reuseFullPreview ? m_TentativeFullAsrRawText : text,
                    reuseFullPreview ? m_TentativeFullAsrText : text);
                m_ChatSample.AcceptText(reuseFullPreview
                    ? m_TentativeFullAsrText
                    : text);
                if (finalClip != null)
                    Destroy(finalClip);
            }
        }

        //清tentative状态。后续轮次重新计数
        m_TentativeFired = false;
        m_TentativeCompletePending = false;
        m_TentativeCompleteText = "";
        m_TentativeUsedFullAsr = false;
        m_TentativeFullAsrResultReady = false;
        m_TentativeFullAsrText = "";
        m_TentativeFullAsrRawText = "";
        m_TentativeSeq++;
        ResetMelodicEouProtection();
        ResetStalledTurnState();
        PrintLog("会话录制结束(T-EOU)...");
    }

    private IEnumerator AwaitTentativeFinalOrFallback(int seq)
    {
        float started = Time.realtimeSinceStartup;
        float timeout = Mathf.Max(1f, m_TentativeFinalReuseTimeoutSeconds);
        while (m_AwaitingTentativeFinal && m_AwaitingTentativeSeq == seq &&
               Time.realtimeSinceStartup - started < timeout)
        {
            yield return null;
        }
        m_AwaitingTentativeFallbackCoroutine = null;
        if (m_AwaitingTentativeFinal && m_AwaitingTentativeSeq == seq)
            FallbackAwaitingTentativeFinal("preview-timeout");
    }

    private void CompleteAwaitingTentativeFinal(string text)
    {
        if (!m_AwaitingTentativeFinal) return;
        if (m_AwaitingTentativeFallbackCoroutine != null)
        {
            StopCoroutine(m_AwaitingTentativeFallbackCoroutine);
            m_AwaitingTentativeFallbackCoroutine = null;
        }

        bool streamingExit = m_AwaitingTentativeStreamingExit;
        AudioClip completedClip = m_AwaitingTentativeFinalClip;
        m_AwaitingTentativeFinal = false;
        m_AwaitingTentativeSeq = -1;
        m_AwaitingTentativeFinalClip = null;
        m_AwaitingTentativeAllowSpeakerLearning = true;
        m_AwaitingTentativeStreamingExit = false;
        if (m_LogTimings)
            Debug.Log("[T-EOU] 同轮预测ASR已晋升为最终结果；未重复分析完整音频");
        // A promoted request may have recovered from its own sealed evidence;
        // do not overwrite that provenance with an empty preview raw text.
        if (m_NeuralVadClient != null &&
            m_NeuralVadClient.LastFinalTranscriptSource != "closed-stream-recovery")
            m_NeuralVadClient.PublishReusedFinalTranscript(CurrentCaptureSerial, m_TentativeFullAsrRawText, text);
        ClearTentativeReuseState(true);
        if (m_ChatSample != null)
            m_ChatSample.AcceptDeferredFinalAsrText(text, streamingExit);
        if (completedClip != null)
            Destroy(completedClip);
    }

    private void FallbackAwaitingTentativeFinal(string reason)
    {
        if (!m_AwaitingTentativeFinal) return;
        if (m_AwaitingTentativeFallbackCoroutine != null)
        {
            StopCoroutine(m_AwaitingTentativeFallbackCoroutine);
            m_AwaitingTentativeFallbackCoroutine = null;
        }

        AudioClip clip = m_AwaitingTentativeFinalClip;
        bool allowSpeakerLearning = m_AwaitingTentativeAllowSpeakerLearning;
        m_AwaitingTentativeFinal = false;
        m_AwaitingTentativeSeq = -1;
        m_AwaitingTentativeFinalClip = null;
        m_AwaitingTentativeAllowSpeakerLearning = true;
        m_AwaitingTentativeStreamingExit = false;
        if (m_LogTimings)
            Debug.LogWarning($"[T-EOU] 预测ASR无法复用({reason})；回落最终完整ASR");
        ClearTentativeReuseState(true);
        if (m_ChatSample != null)
            m_ChatSample.FallbackDeferredFinalAsrToClip(
                clip,
                allowSpeakerLearning);
        else if (clip != null)
            Destroy(clip);
    }

    private void ClearTentativeReuseState(bool advanceSequence)
    {
        m_TentativeFired = false;
        m_TentativePreviewInFlight = false;
        m_TentativeCompletePending = false;
        m_TentativeCompleteText = "";
        m_TentativeUsedFullAsr = false;
        m_TentativeFullAsrResultReady = false;
        m_TentativeFullAsrText = "";
        m_TentativeFullAsrRawText = "";
        if (advanceSequence) m_TentativeSeq++;
    }

    /// <summary>
    /// 用户重新开口或普通语音硬规则触发时调用——
    /// 推进seq让任何还在飞的预测ASR回包失效，同时清tentative标志。
    /// </summary>
    private void InvalidateTentativeEou(string reason)
    {
        //一次预测只推进一次 seq。旧实现保留 inFlight=true 后每帧都会再次推进，
        //造成几十次 user-resumed/AI-started-speaking 抖动。
        if (!m_TentativeFired && !m_TentativeCompletePending) return;
        if (m_LogTentativeEou)
            Debug.Log($"[T-EOU] 失效 ({reason}, seq{m_TentativeSeq}→{m_TentativeSeq + 1})");
        m_TentativeFired = false;
        m_TentativeCompletePending = false;
        m_TentativeCompleteText = "";
        m_TentativeUsedFullAsr = false;
        m_TentativeFullAsrResultReady = false;
        m_TentativeFullAsrText = "";
        m_TentativeFullAsrRawText = "";
        m_TentativeSeq++;
        m_TentativePreviewInFlight = false;
        m_ChatSample?.CancelPreviewAnalysis();
        // Preview dispatch retains its minimum interval; cancellation need not
        // block the next valid snapshot until an obsolete HTTP response returns.
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
            "因为", "如果", "虽然", "嗯", "呃", "啊那", "那么", "或者说", "比如说",
            "像", "像是", "看起来是", "这样的", "这种", "各种", "以及", "还有", "和", "与", "或",
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

        //—— 5) 兜底：让普通语音硬规则决定 ——
        return "ambiguous";
    }

    #endregion

}
