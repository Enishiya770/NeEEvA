using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// SenseVoiceSmall ASR 客户端：通过 HTTP 把录音发给本地 Python 服务。
/// 一次调用同时拿到：转写文本 + 语言 + 情绪 + 音频事件。
///
/// 服务端脚本: Server/SenseVoice/sensevoice_server.py
/// </summary>
public class SenseVoiceSpeechToText : STT
{
    #region 参数定义

    [Header("SenseVoice 服务地址")]
    [SerializeField] private string m_ServerSetting = "http://127.0.0.1:9881";

    [Header("流式 partial（正式结果仍走 /asr）")]
    [SerializeField] private bool m_EnableStreamingPreview = true;
    [Tooltip("留空时由服务地址自动生成 ws://.../stream/asr")]
    [SerializeField] private string m_StreamingPreviewURL = "";
    [SerializeField, Range(400, 2500)] private int m_StreamPartialIntervalMs = 850;
    [SerializeField, Range(400, 3000)] private int m_StreamMinAudioMs = 800;
    [SerializeField] private bool m_LogStreamingPreview = false;

    [Header("把练唱各段与合成源写到 Server/SenseVoice/practice_dumps，供离线分析")]
    //练唱片段只活在内存里：prob 高于 0.70 的轮次不进 band_dumps，事后想复现
    //用户听到的东西就没有素材。开着它，下一次练唱就能在离线侧拿到同一批音频。
    [SerializeField] private bool m_DumpPracticeAudio = true;

    [Header("识别语言: auto / zh / en / ja / ko / yue")]
    [SerializeField] private string m_Language = "auto";

    [Header("把 emotion / event 注入回调文本前缀 —— 例: [情绪:SAD 事件:Laughter] 你好")]
    [SerializeField] private bool m_InjectMetaPrefix = true;

    [Header("跳过这些默认/无意义事件，不注入前缀")]
    [SerializeField] private string[] m_SkipEvents = new string[] { "Speech", "BGM" };

    [Header("跳过这些默认/无意义情绪，不注入前缀")]
    [SerializeField] private string[] m_SkipEmotions = new string[] { "NEUTRAL" };

    [Header("输出详细日志")]
    [SerializeField] private bool m_VerboseLog = false;

    [Header("VAD: 短音频中至少包含多少毫秒人声")]
    [SerializeField] private int m_VadMinSpeechMs = 160;

    [Header("把声纹身份注入给 LLM")]
    [SerializeField] private bool m_InjectSpeakerPrefix = true;

    [Header("听到明确自我介绍时，自动绑定访客姓名")]
    //正则从"我叫X/叫我X"里抓名字自动改声纹档案。**默认关**——它抓的不是自我介绍，
    //是任何含"叫我"的句子。实测同一场对话里造出三个垃圾名字，触发它们的原话全是普通聊天：
    //  「你怎么直接都是这么叫我名字的」        → 抓到「名字」
    //  「一般你是这么叫我的，不太会叫我全名」  → 抓到「的」
    //  「就叫我小优吧」                        → 抓到「小优吧」(语气词已由 StripTrailingParticles 处理)
    //前两个不是语气词问题，剥不掉。角色有 <speaker_name/> 之后这条正则弊大于利：
    //同一场里她只用了 1 次标签，正则却造了 3 个错名字。
    [SerializeField] private bool m_AutoBindIntroducedName = false;

    [Header("歌唱感知 / 角色自主歌曲检索与记忆")]
    [SerializeField] private bool m_EnableSingingAnalysis = true;
    [Tooltip("最近一次歌唱录音保留多久供歌曲检索/记忆使用；音频只发给本机 SenseVoice 服务。")]
    [SerializeField, Range(15f, 600f)] private float m_SingingAudioRetentionSeconds = 180f;

    #endregion

    #region 外部可读的最近一次识别结果 (主业务若想单独取用情绪/事件)

    public string LastText { get; private set; } = "";
    public string LastEmotion { get; private set; } = "";
    public string LastEvent { get; private set; } = "";
    public string LastLanguage { get; private set; } = "";
    public string LastSpeakerId { get; private set; } = "";
    public bool LastNoSpeech { get; private set; } = false;
    public string LastSpeakerName { get; private set; } = "";
    public string LastSpeakerKind { get; private set; } = "";
    /// <summary>最近一次**真的认出**的非 AI 说话人；噪音/无人声轮次不会擦掉它。</summary>
    public string LastKnownSpeakerId { get; private set; } = "";
    public string LastKnownSpeakerName { get; private set; } = "";
    public string LastSpeakerStatus { get; private set; } = "";
    public float LastSpeakerConfidence { get; private set; } = 0f;
    public float LastSpeakerEnrollmentProgress { get; private set; } = 0f;
    public bool LastSpeakerIsNew { get; private set; } = false;
    public bool LastSpeakerPersistent { get; private set; } = false;
    public bool LastIsSinging { get; private set; } = false;
    public float LastSingingProbability { get; private set; } = 0f;
    public float LastPitchStability { get; private set; } = 0f;
    public string LastPitchLowNote { get; private set; } = "";
    public string LastPitchHighNote { get; private set; } = "";
    public string LastNoteSequence { get; private set; } = "";
    public string LastSingingSummary { get; private set; } = "";
    public float[] LastPitchTimelineMidi { get; private set; } = new float[0];
    public float LastPitchTimelineFrameSeconds { get; private set; } = 0.10f;
    public SingingScore LastSingingScore { get; private set; } = null;

    #endregion

    private void Awake()
    {
        m_SpeechRecognizeURL = m_ServerSetting.TrimEnd('/') + "/asr";
        m_VadRecognizeURL = m_ServerSetting.TrimEnd('/') + "/vad";
        m_SongSearchURL = m_ServerSetting.TrimEnd('/') + "/songs/search";
        m_SongRememberURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/remember";
        m_SongRenameURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/rename";
        m_SongForgetURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/forget";
        m_SongSingURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/sing";
    }

    private string m_VadRecognizeURL;
    private string m_SongSearchURL;
    private string m_SongRememberURL;
    private string m_SongRenameURL;
    private string m_SongForgetURL;
    private string m_SongSingURL;
    private byte[] m_LastSingingAudioBytes;
    //置信度分带的暂定边界。**目前只用于打日志，不参与任何判定。**
    //来自 8/8~8/9 两场共 26 次离线判定的分布：
    //   0.13/0.23/0.35 判说话，0.62~0.77 共 14 次全判唱歌，两端各自干净；
    //   [0.40,0.58] 是唯一重叠区(0.40唱 0.45唱 0.57说 0.58说)，占 15%。
    //攒够 60~80 个样本后再决定要不要把「模糊带交给 LLM 定」变成真实行为。
    // 岛比流式起唱点晚多少之内仍然信岛。见下方 onsetsAgree 处的推导。
    private const float k_IslandLaterToleranceSeconds = 1.75f;
    private const float k_SingingBandLow = 0.40f;
    private const float k_SingingBandHigh = 0.58f;

    private static string DescribeSingingBand(float probability)
    {
        if (probability < k_SingingBandLow) return "低区";
        if (probability > k_SingingBandHigh) return "高区";
        return "模糊带";
    }

    private string m_LastSingingLyrics = "";
    //服务端对裁出来那段单独识别得到的歌词。整轮转写含唱前唱后的说话，
    //拿它当歌词会被 SVS 硬塞进几秒的旋律里，唱出来听不清。
    private string m_LastResponseSingingText = "";
    //本轮响应给出的头部裁剪量。每份响应都会重写，所以不会串轮。
    private float m_LastResponseAudioCropSeconds = 0f;
    //岛结束之后被丢掉的那段音频有多长。快速回唱播的是**整条录音**，
    //所以这一段会被原样唱回去——8/25 实测岛只有 0~10.17s、录音 20.95s，
    //后面 10.8 秒的「歌词唱错了，歌词唱错了」被转成她的声线放了回来。
    private float m_LastResponseAudioTailDropSeconds = 0f;
    private string m_LastResponseSingingTailText = "";
    private SongRecall[] m_LastSongRecall = null;
    //最近一次被判为"说话"的转写。用户在唱之前往往会交代这一段是什么
    //（「换一首歌吧」「刚才唱错了，重来一遍」），而那句话正是清单里唯一缺的东西：
    //「第2/2遍」只说明是重复，说不出为什么重复；「疑似」来自旋律匹配，实测 AUC 0.53。
    //8/22 实测：用户两种情况都当面交代过、她也回应说理解了，等到调 practice 时
    //却把两首不同的歌 + 同一句的两遍全合了起来，也没有问。信息丢在了这一步。
    private string m_LastSpokenTranscript = "";

    /// <summary>
    /// 这一轮的曲库候选里有没有写着这个歌名。歌名溯源用——候选是曲库里真实存在的条目。
    /// </summary>
    public bool RecallMentionsSongName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || m_LastSongRecall == null) return false;
        foreach (var item in m_LastSongRecall)
        {
            if (item == null || !item.named) continue;
            if (string.IsNullOrWhiteSpace(item.display_name)) continue;
            if (item.display_name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf(item.display_name, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
    //本轮裁剪起点比岛起点早多少秒。>0 表示歌声之前还留了一段，可能是说话。
    private float m_LastCropLeadInSeconds = 0f;
    public float LastCropLeadInSeconds { get { return m_LastCropLeadInSeconds; } }

    /// <summary>
    /// 唱完之后那截说话的单独转写（可能为空）。整轮 ASR 在长的混合录音上只转得出
    /// 开头——8/10 实测 26.7s 的【说话+日文演唱+说话】只转出了前 6.5 秒，
    /// 尾巴那句「这一段不算，我们重新唱」谁都看不见。回哼要靠它判断你是不是
    /// 当场把刚才那段作废了。
    /// </summary>
    public string LastSingingTailText
    {
        get { return m_LastResponseSingingTailText ?? ""; }
    }

    /// <summary>
    /// 本轮响应的头部裁剪量（秒，0 表示从录音开头就是歌）。流式快速回唱是从
    /// 录音第 0 秒开始预转换的，它必须看这个值：不为 0 就说明它转的那一段
    /// 前面含有最终判定要丢掉的说话。
    /// </summary>
    public float LastResponseAudioCropSeconds
    {
        get { return m_LastResponseAudioCropSeconds; }
    }

    /// <summary>
    /// 本轮响应的尾部丢弃量（秒，0 表示唱到录音结尾）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LastResponseAudioCropSeconds"/> 是一对：头部量防的是"开头那段说话
    /// 被唱出去"，这一个防的是"结尾那段说话被唱出去"。快速回唱两头都不裁，
    /// 所以两个都得看——原来只看了头，8/25 那轮说话在尾巴上，闸门形同虚设。
    /// </remarks>
    public float LastResponseAudioTailDropSeconds
    {
        get { return m_LastResponseAudioTailDropSeconds; }
    }

    /// <summary>
    /// 上一轮里被判为演唱的那一段的单独转写(可能为空)。整轮 ASR 按整轮语言跑，
    /// 演唱段语言不同时会整段丢失，这份是唯一还留着唱词的地方——模态判定要靠它，
    /// 否则「中文说话 + 日文演唱」的轮次在文字上看起来全是说话。
    /// </summary>
    public string LastSegmentLyrics
    {
        get { return m_LastResponseSingingText ?? ""; }
    }
    //与上面那段歌词配套的语言/假名，必须整组一起用，不能和整轮的混着。
    private string m_LastResponseSingingLanguage = "";
    private string m_LastResponseSingingReading = "";
    private string m_LastResponseSingingReadingSource = "";
    private bool m_LastResponseSingingReadingComplete = false;
    private string[] m_LastResponseSingingMora = null;
    private float m_LastSingingAudioTime = -999f;
    //回哼素材的长度下限。不看概率、不看任何阈值，只做一次合理性检查：8/11 那一轮
    //「那你试着唱出来啊」整段判为说(prob=0.42)，仍被缓存成 14 帧 / 1.4s / 9 字的
    //“演唱”，随后她把这句问话本身回哼了出去。两份日志里 9 次缓存，正当素材最短
    //4.7s，唯一的误缓存是 1.4s，中间没有任何一例——3.0s 两边各留一倍余量。
    //命中就整轮不缓存(音频、歌词、旋律、乐谱一起放弃)，保留上一段：只挡旋律会让
    //旧时间线配上新音频，比现在更糟。
    private const float k_MinSingablePerformanceSeconds = 3.0f;
    private float m_LastSingingPerformanceTime = -999f;
    private float[] m_LastSingingPerformanceMidi = new float[0];
    private float m_LastSingingPerformanceFrameSeconds = 0.10f;
    private string m_LastSingingPerformanceLanguage = "";
    private SingingScore m_LastSingingPerformanceScore = null;
    private int m_AsrRequestSerial = 0;
    private int m_LastCompletedAsrSerial = 0;
    private int m_LastSingingCacheSerial = -1;
    private byte[] m_RollbackSingingAudioBytes;
    private string m_RollbackSingingLyrics = "";
    private float m_RollbackSingingAudioTime = -999f;
    private float m_RollbackSingingPerformanceTime = -999f;
    private float[] m_RollbackSingingPerformanceMidi = new float[0];
    private float m_RollbackSingingPerformanceFrameSeconds = 0.10f;
    private string m_RollbackSingingPerformanceLanguage = "";
    private SingingScore m_RollbackSingingPerformanceScore = null;
    // A practice session is intentionally separate from the persistent song catalogue.
    // It keeps only final-ASR-confirmed performances, in the order the user sang them,
    // so ChatSample can later render the practiced phrases as one continuous take.
    private sealed class PracticePhrase
    {
        public byte[] WavBytes;
        public float[] MidiTimeline;
        public float FrameSeconds;
        public string Language;
        public int Signature;
        //身份：用户是按内容指段的(「先唱沉默着走了那段」)，不是按序号。
        //没有这些字段时感知帧只能报歌词片段，她分不清哪几段属于同一首、哪段是最近唱的
        //——8/16 实测练唱会话累到 7 段、跨两首歌，她连着三次选错段(order=1,2 / 3,5,6,1 / 7)。
        public string Lyrics;
        public float Seconds;
        public string SongId;      //本轮曲库回忆的首选，作为"这段属于哪首歌"的线索
        public string SongName;
        public float AtRealtime;   //唱下这一段的时刻，供"最近唱的是哪段"判断
        //唱这一段之前用户说的最后一句话。换歌/重唱的意图就在这句里。
        public string PrecedingSpeech;
        //软降级(声学判唱、文字判说话)写进来的段落带着这一位。
        //它**不拦任何用途**——段落照样能被 order 点到、照样能唱。它只是个警示：
        //这一段有可能根本不是歌声，用户说不是就用 practice_drop 去掉。
        //原来软降级是整个不写，代价是文字判错时那段永远连不起来(8/24 实测四轮说不清)。
        public bool PendingConfirmation;
    }

    /// <summary>练唱会话里每一段的身份，供感知帧展示与顺序指定。</summary>
    public sealed class PracticePhraseInfo
    {
        public int Index;          //1 起，就是 order 里要写的数字
        public string Lyrics;
        public float Seconds;
        public string Language;
        public string SongId;
        public string SongName;
        public float AgoSeconds;   //距现在多久唱的
        //同一句被教了好几遍时的分组：TakeGroup 相同 = 同一句，TakeIndex 是第几遍。
        //没有这两个字段时清单里两段歌词一模一样，用户说"第二次教你的那段"她对不上段号
        //——8/17 实测她因此把「紧闭双眼」连着唱了两遍。
        public int TakeGroup;
        public int TakeIndex;
        public int TakeTotal;
        //唱这一段之前用户说的最后一句话——「换一首歌」「刚才唱错了」都在这里。
        public string PrecedingSpeech;
        //这一段的音高中位(MIDI)，以及它比"各段的共同基准"高/低多少个半音。
        //用户说的「让第三段和前两段调一致」需要这个数才能落地——8/20 实测
        //段1/段2 中位 60，段3 中位 57，实际只差 3 个半音；而她当时猜的是升八度(+12)，
        //既超出 key 的取值范围被截回默认档，也远大于真实差值，于是三轮都听不出变化。
        public float PitchMedianMidi;
        //绝对起调的音名(C#4 这样)。不写「比别段低几个半音」：那需要先认定
        //某几段是共同基调，而用户唱两首歌、或者每段起调都不同时，这个认定
        //就是凭空造出来的。摆绝对值，让用户自己指定以哪段为准。
        public string PitchBaseNote;
        //见 PracticePhrase.PendingConfirmation：清单里要显出来，否则她无从知道
        //哪一段是存疑的，也就不会在用户否认时去 drop 它。
        public bool PendingConfirmation;
    }

    public sealed class PracticeComposition
    {
        public byte[] WavBytes;
        public float[] MidiTimeline;
        public float FrameSeconds;
        public string Language;
        public int PhraseCount;
        public float DurationSeconds;
        public string VariationDiagnostic;
        //这次实际唱出去的段号(1 起，按演唱顺序)。清"待确认"标时必须照它来：
        //order 指名了几段就只有那几段被用户听见，没被唱到的段落不算确认过。
        public List<int> PlayedIndices;
        //以下三项只为"逐段各自移调"服务：转换那一侧一次只收一条音频 + 一个移调值，
        //所以要逐段送就得在拼接**之前**把各段单独拿出来，拼接改到转换之后做。
        //Gaps[i] 是第 i 段之前的静音长度(第 0 段为 0)，照着填才能和整条转的听感一致。
        public List<byte[]> SegmentWavs;
        public List<float> Gaps;
        //各段自己的起调(MIDI)。移调后的结果 = 这个数 + 实际发出的半音数，
        //回报给她之后她才能看出还差多少，而不是一次加一个半音地试。
        public List<float> SegmentMedians;
        //整条的有声音高中位数。auto_f0_adjust 关掉之后要自己补上它本来会给的抬升，
        //公式见 Server/SeedVC/vendor/seed-vc/app_svc.py:315：目标中位 − 源中位。
        public float MedianMidi;
    }

    private readonly List<PracticePhrase> m_PracticePhrases = new List<PracticePhrase>();
    private int m_LastCommittedPracticeSignature = 0;
    private float m_LastPracticeCommitTime = -999f;
    private const int MaxPracticePhraseCount = 16;
    // Final ASR can conservatively label a mixed “spoken lead-in + singing” turn as speech even
    // though streaming analysis already heard stable singing. Keep this response's playable
    // candidate until ChatSample reconciles the two signals in the same callback.
    private byte[] m_LastPlayableCandidateAudioBytes;
    private float m_LastPlayableCandidateTime = -999f;
    private float m_LastPlayableCandidateAudioCropSeconds = 0f;
    private float m_LastPlayableCandidateTimelineCropSeconds = 0f;
    private float m_LastPlayableCandidateScoreCropSeconds = 0f;
    private float m_LastPlayableCandidateAudioEndSeconds = 0f;
    private float m_LastPlayableCandidateTimelineEndSeconds = 0f;
    private float m_LastPlayableCandidateScoreEndSeconds = 0f;

    // WebSocket 的收发在后台线程；Unity UI/MonoBehaviour 回调统一排回主线程。
    private readonly ConcurrentQueue<Action> m_StreamMainThreadActions =
        new ConcurrentQueue<Action>();
    private ClientWebSocket m_StreamSocket;
    private CancellationTokenSource m_StreamCancellation;
    private ConcurrentQueue<StreamOutgoing> m_StreamOutgoing;
    private SemaphoreSlim m_StreamOutgoingSignal;
    private Action<StreamingTranscript> m_StreamPartialCallback;
    private int m_StreamGeneration = 0;
    private int m_StreamQueuedAudioBytes = 0;
    private bool m_StreamStopQueued = false;

    public bool StreamingPreviewEnabled
    {
        get { return m_EnableStreamingPreview; }
    }

    private void Update()
    {
        Action action;
        int budget = 32;
        while (budget-- > 0 && m_StreamMainThreadActions.TryDequeue(out action))
        {
            try { action(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    private void OnDestroy()
    {
        CancelStreamingPreview();
    }

    /// <summary>
    /// 开始一轮只存在于内存中的 partial 会话。它不会修改 LastText、声纹档案或聊天历史。
    /// </summary>
    public bool BeginStreamingPreview(Action<StreamingTranscript> onPartial)
    {
        if (!m_EnableStreamingPreview) return false;

        CancelStreamingPreview();
        int generation = m_StreamGeneration;
        m_StreamPartialCallback = onPartial;
        m_StreamOutgoing = new ConcurrentQueue<StreamOutgoing>();
        m_StreamOutgoingSignal = new SemaphoreSlim(0);
        m_StreamCancellation = new CancellationTokenSource();
        m_StreamSocket = new ClientWebSocket();
        m_StreamQueuedAudioBytes = 0;
        m_StreamStopQueued = false;

        string endpoint = BuildStreamingPreviewURL();
        RunStreamingPreviewAsync(
            generation,
            endpoint,
            m_StreamSocket,
            m_StreamCancellation.Token,
            m_StreamOutgoing,
            m_StreamOutgoingSignal);
        return true;
    }

    /// <summary>
    /// 推送 microphone ring buffer 中“新增加”的交错 float samples。
    /// 服务端固定接收 16kHz mono PCM16；这里负责降混和必要的线性重采样。
    /// </summary>
    public void PushStreamingSamples(float[] interleaved, int channels, int sampleRate)
    {
        if (interleaved == null || interleaved.Length == 0) return;
        if (m_StreamOutgoing == null || m_StreamOutgoingSignal == null || m_StreamStopQueued) return;

        byte[] pcm = FloatToPcm16Mono(interleaved, Mathf.Max(1, channels), Mathf.Max(1, sampleRate));
        if (pcm.Length == 0) return;

        // 连接尚未建立时也允许短暂排队；超过约 8 秒音频说明服务不可用，停止继续堆内存。
        int queued = Interlocked.Add(ref m_StreamQueuedAudioBytes, pcm.Length);
        if (queued > 16000 * 2 * 8)
        {
            Interlocked.Add(ref m_StreamQueuedAudioBytes, -pcm.Length);
            return;
        }

        m_StreamOutgoing.Enqueue(new StreamOutgoing { data = pcm });
        try { m_StreamOutgoingSignal.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 正常结束只用于独立测试；实时对话在 EOU 时调用 Cancel，随后由 /asr 做最终识别。
    /// </summary>
    public void FinishStreamingPreview()
    {
        if (m_StreamOutgoing == null || m_StreamOutgoingSignal == null || m_StreamStopQueued) return;
        m_StreamStopQueued = true;
        m_StreamOutgoing.Enqueue(new StreamOutgoing { text = "{\"event\":\"stop\"}" });
        try { m_StreamOutgoingSignal.Release(); }
        catch (ObjectDisposedException) { }
    }

    public void CancelStreamingPreview()
    {
        m_StreamGeneration++;
        m_StreamPartialCallback = null;
        m_StreamStopQueued = true;
        Interlocked.Exchange(ref m_StreamQueuedAudioBytes, 0);

        try { if (m_StreamCancellation != null) m_StreamCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        try { if (m_StreamSocket != null) m_StreamSocket.Abort(); }
        catch (Exception) { }

        m_StreamCancellation = null;
        m_StreamSocket = null;
        m_StreamOutgoing = null;
        m_StreamOutgoingSignal = null;
    }

    private string BuildStreamingPreviewURL()
    {
        if (!string.IsNullOrWhiteSpace(m_StreamingPreviewURL))
            return m_StreamingPreviewURL.Trim();
        string value = m_ServerSetting.TrimEnd('/');
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "wss://" + value.Substring(8);
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            value = "ws://" + value.Substring(7);
        return value + "/stream/asr";
    }

    private async void RunStreamingPreviewAsync(
        int generation,
        string endpoint,
        ClientWebSocket socket,
        CancellationToken cancellation,
        ConcurrentQueue<StreamOutgoing> outgoing,
        SemaphoreSlim signal)
    {
        try
        {
            await socket.ConnectAsync(new Uri(endpoint), cancellation);
            string start = "{\"event\":\"start\",\"language\":\"" +
                EscapeJson(m_Language) + "\",\"partial_interval_ms\":" +
                Mathf.Clamp(m_StreamPartialIntervalMs, 400, 2500) +
                ",\"min_audio_ms\":" + Mathf.Clamp(m_StreamMinAudioMs, 400, 3000) + "}";
            await SendTextAsync(socket, start, cancellation);

            using (CancellationTokenSource sessionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                Task sender = StreamSendLoopAsync(
                    generation, socket, sessionCancellation.Token, outgoing, signal);
                Task receiver = StreamReceiveLoopAsync(
                    generation, socket, sessionCancellation.Token);
                await Task.WhenAny(sender, receiver);
                // 任一方向结束都终止另一方向，避免服务端报错/关闭后 send loop 永久等 signal。
                sessionCancellation.Cancel();
                try { signal.Release(); } catch (Exception) { }
                try { await Task.WhenAll(sender, receiver); }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (generation == m_StreamGeneration)
            {
                QueueStreamMainThread(() =>
                    Debug.LogWarning("[SenseVoice/stream] 连接中断: " + e.Message));
            }
        }
        finally
        {
            try { socket.Dispose(); } catch (Exception) { }
            try { signal.Dispose(); } catch (Exception) { }
        }
    }

    private async Task StreamSendLoopAsync(
        int generation,
        ClientWebSocket socket,
        CancellationToken cancellation,
        ConcurrentQueue<StreamOutgoing> outgoing,
        SemaphoreSlim signal)
    {
        while (!cancellation.IsCancellationRequested && generation == m_StreamGeneration)
        {
            await signal.WaitAsync(cancellation);
            StreamOutgoing item;
            while (outgoing.TryDequeue(out item))
            {
                if (item.data != null)
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(item.data),
                        WebSocketMessageType.Binary,
                        true,
                        cancellation);
                    Interlocked.Add(ref m_StreamQueuedAudioBytes, -item.data.Length);
                }
                else if (!string.IsNullOrEmpty(item.text))
                {
                    await SendTextAsync(socket, item.text, cancellation);
                    if (item.text.IndexOf("\"stop\"", StringComparison.Ordinal) >= 0)
                        return;
                }
            }
        }
    }

    private async Task StreamReceiveLoopAsync(
        int generation,
        ClientWebSocket socket,
        CancellationToken cancellation)
    {
        byte[] buffer = new byte[8192];
        StringBuilder message = new StringBuilder();
        while (!cancellation.IsCancellationRequested &&
               generation == m_StreamGeneration &&
               socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            string json = message.ToString();
            message.Length = 0;
            StreamResponse response = null;
            try { response = JsonUtility.FromJson<StreamResponse>(json); }
            catch (Exception e)
            {
                if (m_LogStreamingPreview)
                    QueueStreamMainThread(() => Debug.LogWarning("[SenseVoice/stream] JSON: " + e.Message));
            }
            if (response == null) continue;
            if (response.@event == "stopped") return;
            if (response.@event == "error")
            {
                string error = response.error;
                QueueStreamMainThread(() => Debug.LogWarning("[SenseVoice/stream] " + error));
                return;
            }
            if (response.@event != "partial") continue;
            if (string.IsNullOrWhiteSpace(response.text) && !response.is_singing) continue;

            StreamingTranscript transcript = new StreamingTranscript
            {
                Text = response.text ?? "",
                StableText = response.stable_text ?? "",
                UnstableText = response.unstable_text ?? "",
                Language = response.language ?? "",
                Revision = response.revision,
                AudioMs = response.audio_ms,
                Elapsed = response.elapsed,
                IsSinging = response.is_singing,
                SingingProbability = response.singing_probability,
                PitchStability = response.pitch_stability,
            };
            QueueStreamMainThread(() =>
            {
                if (generation != m_StreamGeneration) return;
                if (m_LogStreamingPreview)
                    Debug.Log($"[SenseVoice/stream] {transcript.AudioMs}ms: \"{transcript.Text}\"");
                if (m_StreamPartialCallback != null) m_StreamPartialCallback(transcript);
            });
        }
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellation)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(
            new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation);
    }

    private void QueueStreamMainThread(Action action)
    {
        if (action != null) m_StreamMainThreadActions.Enqueue(action);
    }

    private static string EscapeJson(string value)
    {
        return (value ?? "auto").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static byte[] FloatToPcm16Mono(float[] input, int channels, int sampleRate)
    {
        int frames = input.Length / channels;
        if (frames <= 0) return new byte[0];
        float[] mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            int offset = i * channels;
            for (int c = 0; c < channels; c++) sum += input[offset + c];
            mono[i] = sum / channels;
        }

        float[] output = mono;
        if (sampleRate != 16000)
        {
            int outFrames = Mathf.Max(1, Mathf.RoundToInt(frames * (16000f / sampleRate)));
            output = new float[outFrames];
            float scale = frames > 1 && outFrames > 1 ? (frames - 1f) / (outFrames - 1f) : 0f;
            for (int i = 0; i < outFrames; i++)
            {
                float position = i * scale;
                int left = Mathf.FloorToInt(position);
                int right = Mathf.Min(frames - 1, left + 1);
                output[i] = Mathf.Lerp(mono[left], mono[right], position - left);
            }
        }

        byte[] bytes = new byte[output.Length * 2];
        for (int i = 0; i < output.Length; i++)
        {
            short value = (short)Mathf.RoundToInt(Mathf.Clamp(output[i], -1f, 1f) * 32767f);
            bytes[i * 2] = (byte)(value & 0xff);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xff);
        }
        return bytes;
    }

    private class StreamOutgoing
    {
        public byte[] data;
        public string text;
    }

    public class StreamingTranscript
    {
        public string Text;
        public string StableText;
        public string UnstableText;
        public string Language;
        public bool Revision;
        public int AudioMs;
        public float Elapsed;
        public bool IsSinging;
        public float SingingProbability;
        public float PitchStability;
    }

    [Serializable]
    private class StreamResponse
    {
        public string @event = "";
        public string text = "";
        public string stable_text = "";
        public string unstable_text = "";
        public string language = "";
        public bool revision = false;
        public int audio_ms = 0;
        public float elapsed = 0f;
        public bool is_singing = false;
        public float singing_probability = 0f;
        public float pitch_stability = 0f;
        public string error = "";
    }

    public override void SpeechToText(AudioClip _clip, Action<string> _callback)
    {
        byte[] _audioData = WavUtility.FromAudioClip(_clip);
        StartCoroutine(SendAudioData(_audioData, _callback, true, false, -1f, -1f, false));
    }

    public override void SpeechToText(byte[] _audioData, Action<string> _callback)
    {
        StartCoroutine(SendAudioData(_audioData, _callback, true, false, -1f, -1f, false));
    }

    public void SpeechToText(AudioClip clip, Action<string> callback, bool learnSpeaker)
    {
        SpeechToText(clip, callback, learnSpeaker, false);
    }

    public void SpeechToText(
        AudioClip clip,
        Action<string> callback,
        bool learnSpeaker,
        bool expectSinging,
        float streamingSingingOnsetSeconds = -1f,
        float streamingObservedSeconds = -1f,
        bool streamingSpokenExitDetected = false)
    {
        if (clip == null)
        {
            if (callback != null) callback("");
            return;
        }
        StartCoroutine(SendAudioData(
            WavUtility.FromAudioClip(clip),
            callback,
            learnSpeaker,
            expectSinging,
            streamingSingingOnsetSeconds,
            streamingObservedSeconds,
            streamingSpokenExitDetected));
    }

    /// <summary>
    /// 把短音频发送给本地 FSMN-VAD，在正式 ASR 前判断是否包含人声。
    /// </summary>
    public void CheckVoiceActivity(AudioClip clip, Action<bool> callback)
    {
        CheckVoiceActivityDetailed(clip, false, result =>
        {
            if (callback != null) callback(result != null && result.IsSpeech);
        });
    }

    public void CheckVoiceActivityDetailed(
        AudioClip clip,
        bool speakerCheck,
        Action<VoiceActivityResult> callback)
    {
        if (clip == null)
        {
            if (callback != null) callback(new VoiceActivityResult());
            return;
        }
        CheckVoiceActivityDetailed(WavUtility.FromAudioClip(clip), speakerCheck, callback);
    }

    public void CheckVoiceActivityDetailed(
        byte[] audioBytes,
        bool speakerCheck,
        Action<VoiceActivityResult> callback)
    {
        if (audioBytes == null || audioBytes.Length == 0)
        {
            if (callback != null) callback(new VoiceActivityResult());
            return;
        }
        StartCoroutine(SendVadData(audioBytes, speakerCheck, callback));
    }

    private IEnumerator SendVadData(
        byte[] audioBytes,
        bool speakerCheck,
        Action<VoiceActivityResult> callback)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio_file", audioBytes, "vad_probe.wav", "audio/wav");
        form.AddField("min_speech_ms", Mathf.Clamp(m_VadMinSpeechMs, 80, 2000));
        form.AddField("speaker_check", speakerCheck ? "true" : "false");

        using (UnityWebRequest www = UnityWebRequest.Post(m_VadRecognizeURL, form))
        {
            www.SetRequestHeader("accept", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[SenseVoice/VAD] 请求失败: " + www.error + " / " + www.downloadHandler.text);
                if (callback != null) callback(new VoiceActivityResult());
                yield break;
            }

            VadResponse response = JsonUtility.FromJson<VadResponse>(www.downloadHandler.text);
            VoiceActivityResult result = new VoiceActivityResult();
            if (response != null)
            {
                result.IsSpeech = response.is_speech;
                result.SpeechMs = response.speech_ms;
                result.SpeakerId = response.speaker_id ?? "";
                result.SpeakerName = response.speaker_name ?? "";
                result.SpeakerKind = response.speaker_kind ?? "";
                result.SpeakerStatus = response.speaker_status ?? "";
                result.SpeakerConfidence = response.speaker_confidence;
                result.SelfConfidence = response.speaker_self_confidence;
                result.IsSinging = response.is_singing;
                result.SingingProbability = response.singing_probability;
            }
            if (m_VerboseLog && response != null)
            {
                Debug.Log($"[SenseVoice/VAD] speech={result.IsSpeech} voiced={result.SpeechMs}ms " +
                          $"speaker={result.SpeakerId}/{result.SpeakerKind} " +
                          $"score={result.SpeakerConfidence:F3} self={result.SelfConfidence:F3} " +
                          $"dt={response.elapsed:F3}s");
            }
            if (callback != null) callback(result);
        }
    }

    private IEnumerator SendAudioData(
        byte[] audioBytes,
        Action<string> _callback,
        bool learnSpeaker,
        bool expectSinging,
        float streamingSingingOnsetSeconds,
        float streamingObservedSeconds,
        bool streamingSpokenExitDetected)
    {
        int requestSerial = ++m_AsrRequestSerial;
        stopwatch.Restart();

        WWWForm form = new WWWForm();
        form.AddBinaryData("audio_file", audioBytes, "input.wav", "audio/wav");
        form.AddField("language", m_Language);
        form.AddField("learn_speaker", learnSpeaker ? "true" : "false");
        form.AddField("expect_singing", expectSinging ? "true" : "false");

        using (UnityWebRequest www = UnityWebRequest.Post(m_SpeechRecognizeURL, form))
        {
            www.SetRequestHeader("accept", "application/json");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[SenseVoice] 请求失败: " + www.error + " / " + www.downloadHandler.text);
                if (_callback != null) _callback("");
            }
            else
            {
                string _responseText = www.downloadHandler.text;
                Response _response = JsonUtility.FromJson<Response>(_responseText);

                if (_response == null)
                {
                    Debug.LogError("[SenseVoice] 响应解析失败: " + _responseText);
                    if (_callback != null) _callback("");
                }
                else
                {
                    m_LastCompletedAsrSerial = requestSerial;
                    LastText = _response.text ?? "";
                    LastLanguage = _response.language ?? "";
                    LastEmotion = _response.emotion ?? "";
                    LastEvent = _response.audio_event ?? "";
                    LastSpeakerId = _response.speaker_id ?? "";
                    LastNoSpeech = _response.no_speech;
                    LastSpeakerName = _response.speaker_name ?? "";
                    LastSpeakerKind = _response.speaker_kind ?? "";
                    LastSpeakerStatus = _response.speaker_status ?? "";
                    //粘性身份：只在真的认出人时更新，噪音/无人声轮次不擦掉它。
                    //LastSpeakerId 会被**每一次** ASR 结果覆盖，包括被幻听闸拒绝的轮次
                    //(那时服务端返回 unknown_speaker_meta)。实测后果：她隔一轮才反应过来
                    //要改名字("抱歉，是小悠さん呢")，而那时 LastSpeakerId 已经是 unknown，
                    //<speaker_name/> 被忽略——恰好在这个标签最该起作用的纠错场景上失效。
                    if (!string.IsNullOrEmpty(LastSpeakerId) && LastSpeakerId != "unknown" &&
                        LastSpeakerId != "ai_self" && LastSpeakerKind != "ai")
                    {
                        LastKnownSpeakerId = LastSpeakerId;
                        LastKnownSpeakerName = LastSpeakerName;
                    }
                    LastSpeakerConfidence = _response.speaker_confidence;
                    LastSpeakerEnrollmentProgress = _response.speaker_enrollment_progress;
                    LastSpeakerIsNew = _response.speaker_is_new;
                    LastSpeakerPersistent = _response.speaker_persistent;
                    LastIsSinging = m_EnableSingingAnalysis && _response.is_singing;
                    LastSingingProbability = _response.singing_probability;
                    LastPitchStability = _response.pitch_stability;
                    LastPitchLowNote = _response.pitch_low_note ?? "";
                    LastPitchHighNote = _response.pitch_high_note ?? "";
                    LastNoteSequence = _response.note_sequence ?? "";
                    LastSingingSummary = _response.singing_summary ?? "";
                    LastSingingScore = _response.singing_score;
                    //分带观测：只记录，不改判定。看两端是否真的干净、模糊带多大比例，
                    //以及模糊带里若要问 LLM，手里的文本长什么样。
                    if (m_EnableSingingAnalysis && !_response.no_speech &&
                        _response.singing_analysis_available)
                    {
                        string band = DescribeSingingBand(_response.singing_probability);
                        //岛秒数**只观测不参与判定**。8/12 那一轮「好吧…那个我们换一首歌吧…
                        //简单点，说话的方式简单点…」整段 24 秒里唱了 9.14 秒，岛检测找到了，
                        //但整段概率被前面 13 秒说话稀释到 0.40 判成说话——用户连唱三遍才被听见。
                        //现有 17 个样本里，真唱的岛秒数最低 4.97、纯说话最高 2.44，中间是分开的，
                        //但"该放行却被判说"的正样本只有 1 例，撑不起一个具体阈值。
                        //所以先把它打出来攒样本，别重蹈 _ISLAND_PROB_FLOOR 那次一路改阈值的覆辙。
                        //整段概率之外单独看它，是因为混合轮的均值天然会被说话拉低。
                        int timelineFrames = _response.pitch_timeline_midi != null
                            ? _response.pitch_timeline_midi.Length : 0;
                        float contentSecondsProbe = _response.pitch_timeline_start_seconds +
                            timelineFrames * Mathf.Max(0.02f, _response.pitch_timeline_frame_seconds);
                        float islandEndProbe = _response.singing_end_seconds > 0f
                            ? _response.singing_end_seconds : contentSecondsProbe;
                        float islandSecondsProbe = Mathf.Max(
                            0f, islandEndProbe - _response.singing_start_seconds);
                        Debug.Log($"[Singing/Band] 离线 prob={_response.singing_probability:F2} " +
                                  $"stab={_response.pitch_stability:F2} → {band} " +
                                  $"(阈值 0.58 判为{(_response.is_singing ? "唱" : "说")}) " +
                                  $"岛={islandSecondsProbe:F2}s/内容{contentSecondsProbe:F2}s " +
                                  $"文本=\"{(LastText ?? "").Trim()}\"");
                    }
                    //说话轮的原话留一份，下一段歌声提交时作为"唱这段之前用户说了什么"。
                    if (!_response.is_singing)
                    {
                        string spoken = (LastText ?? "").Trim();
                        //太短的应答（「嗯」「好」）说明不了任何事，留着反而占地方。
                        if (spoken.Length >= 4) m_LastSpokenTranscript = spoken;
                    }
                    //每一份响应都要重置：调用方问的是「刚刚这一轮裁了多少头」，
                    //沿用上一轮的值会让没有演唱的轮次继承一个大裁剪量。
                    m_LastResponseAudioCropSeconds = 0f;
                    m_LastResponseAudioTailDropSeconds = 0f;
                    m_LastResponseSingingTailText = _response.singing_tail_text ?? "";
                    //每轮必赋值：沿用上一轮会让这一次的歌声配上别的歌的回忆
                    m_LastSongRecall = _response.song_recall;
                    m_LastResponseSingingText = _response.singing_text ?? "";
                    m_LastResponseSingingLanguage = _response.singing_language ?? "";
                    m_LastResponseSingingReading = _response.singing_lyrics_reading ?? "";
                    m_LastResponseSingingReadingSource =
                        _response.singing_lyrics_reading_source ?? "";
                    m_LastResponseSingingReadingComplete =
                        _response.singing_lyrics_reading_complete;
                    m_LastResponseSingingMora = _response.singing_lyrics_mora;
                    LastPitchTimelineMidi = _response.pitch_timeline_midi != null &&
                        _response.pitch_timeline_midi.Length > 0
                        ? _response.pitch_timeline_midi
                        : (_response.pitch_contour_midi ?? new float[0]);
                    LastPitchTimelineFrameSeconds = Mathf.Clamp(
                        _response.pitch_timeline_frame_seconds > 0f
                            ? _response.pitch_timeline_frame_seconds
                            : 0.10f,
                        0.02f,
                        0.25f);
                    float acousticAudioCropSeconds = Mathf.Max(0f,
                        _response.audio_content_start_seconds + _response.singing_start_seconds);
                    float audioCropSeconds = acousticAudioCropSeconds;
                    float rawAudioSeconds = GetWavDurationSeconds(audioBytes);
                    float protectedStreamingCropSeconds = -1f;
                    bool onsetsAgree = false;
                    bool hasUsableStreamingOnset = streamingSingingOnsetSeconds >= 0f &&
                        streamingObservedSeconds > 0f &&
                        streamingSingingOnsetSeconds <= streamingObservedSeconds + 0.5f &&
                        (rawAudioSeconds <= 0f || streamingSingingOnsetSeconds <= rawAudioSeconds + 0.5f);
                    if (hasUsableStreamingOnset)
                    {
                        // Streaming and final WAV both begin at the recording start.  Keep a
                        // short lead-in before the first stable singing evidence, and never let
                        // the offline longest-island heuristic cut later than that anchor.
                        protectedStreamingCropSeconds = Mathf.Max(
                            0f, streamingSingingOnsetSeconds - 0.75f);
                        // 0.75s 的提前量是为了防止岛跳得太晚。两者本来就一致时它没有
                        // 存在理由，只会往回吃进约一秒的说话：8/9 实测 岛=3.93s /
                        // 流式=3.65s(差 0.28s)，减完变成 2.90s，「那我再唱最后一段哦」
                        // 就这么漏进了回哼素材。
                        // 一致时取岛的精确值；分歧大时(岛可能跳到后半句)维持保护。
                        //
                        // 判据只该看**岛比流式晚多少**，不该看绝对差：
                        //  · 岛比流式早 → min() 本来就会选岛，保护不起作用，早多少无所谓
                        //    (8/10 实测 岛=13.83s / 流式=20.41s，早 6.6s，选岛且正确)；
                        //  · 岛比流式晚 → 才是"岛可能跳到后半句"，保护才有意义。
                        // 原来写成 Abs(...) <= 1.0f，把两种情形混为一谈，而且 1.0 这个
                        // 硬阈值有悬崖：8/10 实测 岛=8.33s / 流式=7.27s，差 1.06s，
                        // 只超了 0.06 就判为分歧，退回 6.52s，把 1.81 秒的「唱着首」
                        // 当成歌唱了回去(切片送 ASR 复核：6.52-8.33s 是说话 prob=0.36，
                        // 8.33s 起才是歌 prob=0.71，岛是对的)。
                        //
                        // 容忍度 1.75s 的依据很薄，只有两个方向相反的实测点：
                        //   晚 1.06s → 岛正确(上面这次)
                        //   晚 2.50s → 岛错误(8/9，岛=11.43s / 流式=8.93s，占比 33%)
                        // 取在两者之间。再遇到反例应该换判据，而不是继续挪这个数。
                        float islandLaterBySeconds =
                            acousticAudioCropSeconds - streamingSingingOnsetSeconds;
                        onsetsAgree = islandLaterBySeconds <= k_IslandLaterToleranceSeconds;
                        audioCropSeconds = onsetsAgree
                            ? acousticAudioCropSeconds
                            : Mathf.Min(
                                acousticAudioCropSeconds, protectedStreamingCropSeconds);
                    }
                    bool conservativeHeadKeep = false;
                    if (!hasUsableStreamingOnset && expectSinging && LastIsSinging)
                    {
                        // An expected sing-along is allowed to be conservative.  The offline
                        // longest-island detector can jump to the second phrase when streaming
                        // did not reach its stable-singing gate (the July 22 log cut 9.83 s this
                        // way).  With no independent onset anchor, preserving the complete take
                        // is safer than silently deleting a real opening; a short breath or
                        // spoken lead-in is an acceptable trade-off.
                        audioCropSeconds = 0f;
                        conservativeHeadKeep = true;
                    }

                    // 曾经在这里让「有分段歌词时一律按岛裁」，已撤回。
                    // 它确实修好了「13 个假名摊到含 7.5s 说话的音频上」，但代价是废掉了
                    // 流式保护那条 min(acoustic, streamOnset-0.75)——而那条正是防止岛切
                    // 得太晚的。8/9 实测 acoustic=11.43s / streamOnset=8.93s / 占比 33%，
                    // 岛跳到了后半句，保护本已拦住，被这行推翻后切掉了 3.25s 真歌声。
                    // 两次失效方向相反(岛太早 vs 岛太晚)，占比 45% 与 33% 分不开，
                    // 暂无可靠判据，先退回已知状态：窗口由流式保护决定。
                    bool alignCropToIsland = false;

                    // 音频与音高时间线必须描述同一段。时间线只从 pitch_timeline_start_seconds
                    // 开始（服务端只为歌声那部分建时间线），如果音频裁得比它还靠前，多出来的
                    // 那截就没有旋律与之对应——8/9 实测保守分支把 audioCrop 归零，音频留了
                    // 19.16s、时间线只有 9.90s，排队时按 melody=9.9s 记账，实际播出去 19.10s，
                    // 前面 9.3 秒的说话被原样复读。
                    float timelineStartAbsolute = _response.audio_content_start_seconds +
                        _response.pitch_timeline_start_seconds;
                    bool clampedToTimeline = audioCropSeconds < timelineStartAbsolute - 0.05f;
                    if (clampedToTimeline) audioCropSeconds = timelineStartAbsolute;

                    float croppedContentSeconds = Mathf.Max(
                        0f, audioCropSeconds - _response.audio_content_start_seconds);
                    float timelineCropSeconds = Mathf.Max(
                        0f, croppedContentSeconds - _response.pitch_timeline_start_seconds);

                    // 尾部边界：唱完之后接的那段说话必须切掉，否则会被当成歌词唱回去
                    // （实测她把「我唱完后说的话」原样复读了出来）。服务端找不到可信
                    // 边界时回填整段时长，贴到录音末尾即视为无需裁剪。
                    float audioEndSeconds = 0f;
                    if (_response.singing_end_seconds > 0f)
                    {
                        float absoluteEnd = _response.audio_content_start_seconds +
                            _response.singing_end_seconds;
                        if (absoluteEnd > audioCropSeconds + 1.2f &&
                            (rawAudioSeconds <= 0f || absoluteEnd < rawAudioSeconds - 0.45f))
                            audioEndSeconds = absoluteEnd;
                    }
                    float contentEndSeconds = audioEndSeconds > 0f
                        ? Mathf.Max(0f, audioEndSeconds - _response.audio_content_start_seconds)
                        : 0f;
                    float timelineEndSeconds = contentEndSeconds > 0f
                        ? Mathf.Max(0f, contentEndSeconds - _response.pitch_timeline_start_seconds)
                        : 0f;
                    // The tail text is authoritative even when no explicit sing-along request
                    // was armed. Otherwise a spontaneous sung phrase followed by "不会唱了"
                    // could still overwrite the last clean performance cache.
                    bool endsWithSpokenSingingExit = streamingSpokenExitDetected ||
                        EndsWithSpokenSingingExit(LastText);

                    //裁剪三行必须一直打：8/9 那次「说话+歌唱」被整段复读，服务端明明
                    //把起唱点定在 4.53s，Unity 侧却记的是 head=0.00s——而 crop 那行被
                    //挂在 expectSinging 下，自发歌唱(本场绝大多数)时不打，等于丢掉了唯一
                    //能看出是谁把裁剪抹掉的证据。条件放宽到「本轮有歌声分析且确实算出了
                    //歌唱或非零起点」，普通说话轮仍然不打。
                    bool logCropDiagnostics = _response.singing_analysis_available &&
                        (LastIsSinging || acousticAudioCropSeconds > 0.05f);
                    if (expectSinging || m_VerboseLog)
                    {
                        Debug.Log($"[SenseVoice/Singing] final expected={expectSinging} " +
                                  $"singing={LastIsSinging} prob={LastSingingProbability:F2} " +
                                  $"stability={LastPitchStability:F2} voiced={_response.voiced_ratio:F2} " +
                                  $"sustained={_response.sustained_ratio:F2} " +
                                  $"timeline={LastPitchTimelineMidi.Length} " +
                                  $"spokenExit={endsWithSpokenSingingExit} " +
                                  $"streamExitHint={streamingSpokenExitDetected}");
                    }
                    if (logCropDiagnostics)
                    {
                        Debug.Log($"[SenseVoice/Singing] crop raw={rawAudioSeconds:F2}s " +
                                  $"acoustic={acousticAudioCropSeconds:F2}s " +
                                  $"streamOnset={streamingSingingOnsetSeconds:F2}s " +
                                  $"streamObserved={streamingObservedSeconds:F2}s " +
                                  $"protected={protectedStreamingCropSeconds:F2}s " +
                                  $"applied={audioCropSeconds:F2}s " +
                                  $"起点一致={onsetsAgree} " +
                                  $"timeline={timelineCropSeconds:F2}s " +
                                  $"对齐时间线={clampedToTimeline}" +
                                  (clampedToTimeline ? $"(→{timelineStartAbsolute:F2}s)" : ""));
                        Debug.Log($"[SenseVoice/Singing] tail rawEnd={_response.singing_end_seconds:F2}s " +
                                  $"contentStart={_response.audio_content_start_seconds:F2}s " +
                                  $"applied={audioEndSeconds:F2}s " +
                                  $"timeline={timelineEndSeconds:F2}s");
                        // 岛占内容的比例。保守分支(流式没确认起唱点时放弃头部裁剪)防的是
                        // 「岛跳到第二句」——那种失效会表现为比例很小。但 8/9 实测里占比 44%、
                        // 岛起点完全正确的一轮也被它放弃了，所以先量分布再定阈值，别拍脑袋。
                        float contentSeconds = _response.pitch_timeline_start_seconds +
                            LastPitchTimelineMidi.Length * LastPitchTimelineFrameSeconds;
                        float islandEnd = _response.singing_end_seconds > 0f
                            ? _response.singing_end_seconds : contentSeconds;
                        float islandSeconds = Mathf.Max(
                            0f, islandEnd - _response.singing_start_seconds);
                        //起点分歧观测：真正拿去变声的是 audioCropSeconds，而岛检测认为
                        //歌声从 singing_start_seconds 才开始。两者差得越多，前面混进去的
                        //说话就越长。8/20 实测一轮 applied=3.75s 而岛起点=7.23s，中间那
                        //3.48 秒是用户的中文说话，被原样用歌声唱了回去；用户连着三轮抱怨
                        //「你把我说话的部分也复读出来了」，而工具结果只写"成功播放"，
                        //她完全无从察觉，最后归结成「それは仕方がないの」。
                        //先只观测不改判据——保守取早本身是对的(防止乐句开头被切)，
                        //要区分"多留半秒余量"和"多留三秒说话"需要样本。
                        m_LastCropLeadInSeconds = Mathf.Max(
                            0f, _response.singing_start_seconds - audioCropSeconds);
                        Debug.Log($"[Singing/LeadIn] 裁剪起点={audioCropSeconds:F2}s " +
                                  $"岛起点={_response.singing_start_seconds:F2}s " +
                                  $"多留={m_LastCropLeadInSeconds:F2}s 起点一致={onsetsAgree} " +
                                  $"整轮{(LastText ?? "").Trim().Length}字 " +
                                  $"分段{(_response.singing_text ?? "").Trim().Length}字");
                        Debug.Log($"[SenseVoice/Singing] island 占比 " +
                                  $"{(contentSeconds > 0.01f ? islandSeconds / contentSeconds : 0f):P0} " +
                                  $"({islandSeconds:F2}s / 内容 {contentSeconds:F2}s) " +
                                  $"起点={_response.singing_start_seconds:F2}s " +
                                  $"保守放弃头部裁剪={conservativeHeadKeep} " +
                                  $"为对齐歌词改按岛裁={alignCropToIsland}");
                    }

                    m_LastResponseAudioCropSeconds = audioCropSeconds;
                    m_LastResponseAudioTailDropSeconds =
                        audioEndSeconds > 0f && rawAudioSeconds > audioEndSeconds
                            ? rawAudioSeconds - audioEndSeconds
                            : 0f;

                    bool hasPlayablePitch = HasPlayablePitchTimeline(LastPitchTimelineMidi);
                    if (hasPlayablePitch && !endsWithSpokenSingingExit)
                    {
                        m_LastPlayableCandidateAudioBytes = audioBytes;
                        m_LastPlayableCandidateTime = Time.realtimeSinceStartup;
                        m_LastPlayableCandidateAudioCropSeconds = audioCropSeconds;
                        m_LastPlayableCandidateTimelineCropSeconds = timelineCropSeconds;
                        m_LastPlayableCandidateScoreCropSeconds = croppedContentSeconds;
                        m_LastPlayableCandidateAudioEndSeconds = audioEndSeconds;
                        m_LastPlayableCandidateTimelineEndSeconds = timelineEndSeconds;
                        m_LastPlayableCandidateScoreEndSeconds = contentEndSeconds;
                    }

                    if (LastIsSinging && !endsWithSpokenSingingExit)
                    {
                        CacheLastSingingPerformance(
                            audioBytes,
                            audioCropSeconds,
                            timelineCropSeconds,
                            croppedContentSeconds,
                            audioEndSeconds,
                            timelineEndSeconds,
                            contentEndSeconds);
                    }
                    else if (LastIsSinging && endsWithSpokenSingingExit)
                    {
                        Debug.Log("[SenseVoice/Singing] 检出末尾口语退出语义；" +
                                  "保留上一段有效歌声，不缓存本轮混合音频");
                    }

                    if (LastNoSpeech || (string.IsNullOrWhiteSpace(LastText) && !LastIsSinging))
                    {
                        if (m_VerboseLog)
                        {
                            Debug.Log($"[SenseVoice] no-speech rejected evt={LastEvent} " +
                                      $"voiced={_response.speech_ms}ms vad={_response.vad_elapsed:F3}s");
                        }
                        if (_callback != null) _callback("");
                        yield break;
                    }

                    if (m_AutoBindIntroducedName && LastSpeakerKind == "guest")
                    {
                        string introducedName = ExtractIntroducedName(LastText);
                        if (!string.IsNullOrEmpty(introducedName))
                        {
                            LastSpeakerName = introducedName;
                            RenameSpeaker(LastSpeakerId, introducedName, null);
                        }
                    }

                    string finalText = BuildLastPerceivedText();

                    if (m_VerboseLog)
                    {
                        Debug.Log($"[SenseVoice] text=\"{LastText}\" lang={LastLanguage} " +
                                  $"emo={LastEmotion} evt={LastEvent} " +
                                  $"speaker={LastSpeakerName}/{LastSpeakerId} " +
                                  $"score={LastSpeakerConfidence:F3} status={LastSpeakerStatus} " +
                                  $"progress={LastSpeakerEnrollmentProgress:P0} dt={_response.elapsed:F2}s");
                    }

                    if (_callback != null) _callback(finalText);
                }
            }
        }

        stopwatch.Stop();
        Debug.Log("SenseVoice 语音识别总耗时：" + stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// 根据 emotion / event 生成要注入到用户消息前面的元数据前缀。
    /// 情绪为 NEUTRAL、事件为 Speech/BGM 时不注入（噪声太多无意义）。
    /// </summary>
    private string BuildMetaPrefix()
    {
        bool hasEmo = !string.IsNullOrEmpty(LastEmotion) && !IsInArray(LastEmotion, m_SkipEmotions);
        bool hasEvt = !string.IsNullOrEmpty(LastEvent) && !IsInArray(LastEvent, m_SkipEvents);
        if (!hasEmo && !hasEvt) return "";

        string inside = "";
        if (hasEmo) inside += "情绪:" + LastEmotion;
        if (hasEmo && hasEvt) inside += " ";
        if (hasEvt) inside += "事件:" + LastEvent;
        return "[" + inside + "] ";
    }

    private string BuildSpeakerPrefix()
    {
        if (string.IsNullOrEmpty(LastSpeakerId) || LastSpeakerId == "unknown")
            return "[说话人:无法确认] ";

        string name = string.IsNullOrEmpty(LastSpeakerName) ? LastSpeakerId : LastSpeakerName;
        string status = LastSpeakerStatus == "candidate"
            ? $"; 注册进度:{LastSpeakerEnrollmentProgress:P0}"
            : "";
        return $"[说话人:{name}; speaker_id:{LastSpeakerId}; 类型:{LastSpeakerKind}; " +
               $"可信度:{LastSpeakerConfidence:F2}{status}] ";
    }

    private string BuildSingingPrefix()
    {
        string range = (!string.IsNullOrEmpty(LastPitchLowNote) && !string.IsNullOrEmpty(LastPitchHighNote))
            ? LastPitchLowNote + "～" + LastPitchHighNote
            : "未知";
        string melody = string.IsNullOrEmpty(LastNoteSequence)
            ? "不足以形成音符序列"
            : LastNoteSequence;
        return $"[演唱片段; 歌唱概率:{LastSingingProbability:F2}; 语言:{LastLanguage}; " +
               $"音域:{range}; 音高稳定度:{LastPitchStability:F2}; 旋律:{melody}; " +
               "歌词是ASR推测，长音与一字多音处可能不准确" +
               BuildSongRecallClause() + "] ";
    }

    /// <summary>
    /// 把曲库回忆拼成感知帧里的一小段。措辞刻意留在"像/也许"这一档：
    /// 旋律相似度分不开不同的歌，这几条只是线索，断言留给她看完歌词再下。
    /// </summary>
    private string BuildSongRecallClause()
    {
        if (m_LastSongRecall == null || m_LastSongRecall.Length == 0)
            return "; 曲库里没有旋律接近的段落";
        var sb = new StringBuilder("; 曲库里旋律接近的(仅线索，不是识别结果):");
        int shown = 0;
        foreach (var item in m_LastSongRecall)
        {
            if (item == null || string.IsNullOrEmpty(item.song_id)) continue;
            if (shown >= 3) break;
            shown++;
            string name = item.named && !string.IsNullOrWhiteSpace(item.display_name)
                ? "《" + item.display_name.Trim() + "》"
                : "未命名";
            string lyric = (item.lyrics ?? "").Trim();
            if (lyric.Length > 24) lyric = lyric.Substring(0, 24) + "…";
            sb.Append($" {shown}) {name} id={item.song_id}");
            if (lyric.Length > 0) sb.Append($" 歌词\"{lyric}\"");
            if (!string.IsNullOrWhiteSpace(item.note_sequence))
                sb.Append($" 旋律{item.note_sequence.Trim()}");
            sb.Append($" 相似{item.confidence:F2}");
            //含有率单独给：它回答的是"我唱的这几个音在这首里找到了多少"，
            //和"这两段像不像"是不同的问题，用户唱一小段时前者才是有意义的那个。
            if (item.containment > 0.001f)
                sb.Append($" 音符命中{item.containment:P0}");
            if (item.last_heard > 0)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(item.last_heard).ToLocalTime();
                sb.Append($" 上次听到{when:M月d日 HH:mm}");
            }
            if (item.take_count > 1) sb.Append($" 共{item.take_count}遍");
        }
        return shown == 0 ? "; 曲库里没有旋律接近的段落" : sb.ToString();
    }

    /// <summary>
    /// Rebuild the user-facing/LLM-facing text after ChatSample reconciles a strong streaming
    /// singing signal with a conservative final classification.
    /// </summary>
    public string BuildLastPerceivedText()
    {
        string perceivedText = string.IsNullOrWhiteSpace(LastText)
            ? "（没有识别出歌词的哼唱片段）"
            : LastText;
        return (m_InjectSpeakerPrefix ? BuildSpeakerPrefix() : "")
            + (m_InjectMetaPrefix ? BuildMetaPrefix() : "")
            + (LastIsSinging ? BuildSingingPrefix() : "")
            + perceivedText;
    }

    /// <summary>
    /// The final analyser intentionally uses a stricter singing threshold than streaming mode.
    /// If streaming already crossed its stable threshold and this exact final response contains
    /// a playable pitch track, promote it instead of discarding the melody at the hand-off.
    /// </summary>
    public bool PromoteLastSingingPerformanceFromStreaming(
        float streamingProbability,
        float streamingPitchStability)
    {
        if (EndsWithSpokenSingingExit(LastText)) return false;
        if (!m_EnableSingingAnalysis || LastNoSpeech || LastIsSinging) return LastIsSinging;
        //stab 曾在 8/12 被我从判据里拿掉(理由是它没有区分度、等于长期为真)，
        //当天下一场就证明那是个错误改动：新的误判走的是服务端 expect_singing
        //放宽那条路，与提升点无关——改动对故障零贡献，却关掉了"离线保守、
        //流式 0.40~0.55 的真唱"这条救援通道，纯亏。而且当时日志里提升点总共
        //只触发过 1 次，等于拿 1 个样本改判据。已回退。
        //真正的修复在转换期否决(见 ChatSample 的 SVC 期间语义复核)：
        //声学侧继续宽松地抢时间，语义侧在音频落地前行使否决权。
        bool strongStreamingEvidence = streamingProbability >= 0.55f ||
            streamingPitchStability >= 0.52f;
        //离线概率落在低区时，不允许流式把它推翻。
        //流式是 quick 探针 + 半截音频，系统性高估：8/9 实测「那我们换一首歌吧。」
        //流式 0.68 而离线 0.13，差 0.55；这一步三次触发三次都把正确的离线结论推翻了，
        //其中一次还经由 armed-sing-along 快速路径直接唱了出来(那条路不问 LLM)。
        //离线看的是完整音频，低区意味着它有把握——只有它自己也不确定(模糊带)时，
        //流式证据才有资格参与。上界不必判：离线 >= 阈值时 LastIsSinging 已为真，
        //函数在前面就返回了，所以这里生效的区间恰好就是模糊带。
        bool offlineAllowsPromotion = LastSingingProbability >= k_SingingBandLow;
        //分带观测：这一步会用流式概率推翻离线结论，是 8/9 那次「你跟着我唱呀」和
        //8/12 那次「那我那我开始喽」被当成唱歌的实际放行口。stab 已经不参与判定，
        //但继续打出来——它当初被怀疑"长期为真"，留着看这个怀疑还成不成立。
        Debug.Log($"[Singing/Band] 提升点 streamProb={streamingProbability:F2} " +
                  $"→ {DescribeSingingBand(streamingProbability)}  " +
                  $"(判据 prob>=0.55: {streamingProbability >= 0.55f}) " +
                  $"streamStab={streamingPitchStability:F2}(仅观测, " +
                  $"旧判据下会={streamingPitchStability >= 0.52f}) " +
                  $"离线prob={LastSingingProbability:F2}(判说话) " +
                  $"文本=\"{(LastText ?? "").Trim()}\"");
        bool freshCandidate = Time.realtimeSinceStartup - m_LastPlayableCandidateTime <= 5f &&
            m_LastPlayableCandidateAudioBytes != null &&
            m_LastPlayableCandidateAudioBytes.Length > 44;
        if (!offlineAllowsPromotion)
        {
            Debug.Log($"[Singing/Band] 提升被拒：离线 prob={LastSingingProbability:F2} " +
                      $"落在低区(<{k_SingingBandLow:F2})，不接受流式 {streamingProbability:F2} 的推翻");
            return false;
        }
        if (!strongStreamingEvidence || !freshCandidate ||
            !HasPlayablePitchTimeline(LastPitchTimelineMidi))
            return false;
        //提升是拿流式证据推翻离线的“判说”，素材再短就什么都撑不住了。8/11
        //「那你试着唱出来啊」正是从这里进去的：离线 0.42 判说，流式把它提成歌唱，
        //缓存下 1.4s / 9 字，随后她把这句问话本身回哼了出去。长度不达标就连
        //LastIsSinging 也不置真，否则这一轮会被当成“她唱过”而缓存里却是上一段。
        float promotableSeconds = MeasurePerformanceSeconds(
            m_LastPlayableCandidateTimelineCropSeconds,
            m_LastPlayableCandidateTimelineEndSeconds);
        if (promotableSeconds < k_MinSingablePerformanceSeconds)
        {
            Debug.Log($"[Singing/Band] 提升被拒：可唱素材只有 {promotableSeconds:F2}s，" +
                      $"短于 {k_MinSingablePerformanceSeconds:F1}s");
            return false;
        }

        LastIsSinging = true;
        LastSingingProbability = Mathf.Max(LastSingingProbability, streamingProbability);
        LastPitchStability = Mathf.Max(LastPitchStability, streamingPitchStability);
        CacheLastSingingPerformance(
            m_LastPlayableCandidateAudioBytes,
            m_LastPlayableCandidateAudioCropSeconds,
            m_LastPlayableCandidateTimelineCropSeconds,
            m_LastPlayableCandidateScoreCropSeconds,
            m_LastPlayableCandidateAudioEndSeconds,
            m_LastPlayableCandidateTimelineEndSeconds,
            m_LastPlayableCandidateScoreEndSeconds);
        Debug.Log($"[SenseVoice/Singing] 最终判定由流式证据恢复为歌唱 " +
                  $"prob={LastSingingProbability:F2} stability={LastPitchStability:F2} " +
                  $"timeline={m_LastSingingPerformanceMidi.Length}");
        return true;
    }

    /// <summary>
    /// Detects a user who stops a sung phrase and switches to ordinary speech at the end
    /// of the same microphone turn ("不会唱了", "forgot the rest", etc.). The tail
    /// constraint avoids suppressing a later performance merely because the user said
    /// "I cannot sing" before starting it.
    /// </summary>
    /// <summary>
    /// "唱错了"前面最近的那个人称是第二/第三人称时，这句在说对方，不是用户自陈。
    /// </summary>
    /// <remarks>
    /// 比"紧挨着"宽：「你**刚才**唱错了呢」中间隔着两个字，只看相邻会漏。
    /// 也比"窗口里出现过你"严：「你听我唱错了没」里 你 和 我 都在，
    /// 谁离得近就算谁——离动词最近的那个才是主语。
    /// 一个人称都没有时按自陈算（「唱错了，重来」是最常见的说法）。
    /// </remarks>
    private static bool IsSecondPersonBlame(string lower, int index)
    {
        int window = Math.Min(index, 8);
        if (window <= 0) return false;
        string before = lower.Substring(index - window, window);
        int other = LastIndexOfAny(before, new[] { "你", "您", "她", "他", "you ", "she " });
        if (other < 0) return false;
        int self = LastIndexOfAny(before, new[] { "我", "咱", "俺", "i " });
        return self < other;   //自称更靠近动词时算自陈
    }

    private static int LastIndexOfAny(string text, string[] needles)
    {
        int best = -1;
        foreach (string n in needles)
        {
            int at = text.LastIndexOf(n, StringComparison.Ordinal);
            if (at > best) best = at;
        }
        return best;
    }

    public static bool EndsWithSpokenSingingExit(string text)
    {
        string lower = (text ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(lower)) return false;
        string[] phrases =
        {
            "不会唱", "不唱了", "不唱啦", "不唱", "唱不下去", "唱不下来了",
            "唱不上去", "唱不上来", "唱不上", "唱不了", "唱不出来", "唱不动了",
            "这个音唱不上", "高音唱不上", "嗓子唱不上", "够不到这个音",
            "后面不会", "后面不记得", "后面忘了", "后面的忘了", "不会后面",
            "忘词", "歌词忘了", "歌词不记得", "先不唱", "先停一下", "停一下",
            "就会这么多", "只会这么多", "只能唱到这里", "到这里吧", "算了不唱",
            //「唱错了」和「唱不下去」是两类事：前者还能继续唱，只是要作废这一遍。
            //上面整张表只认后者，于是 8/25 那轮「等那个歌词唱错了」一个词都没命中，
            //spokenExit=False，整条 20.95s 录音(含这句话本身)被原样唱了回去。
            "唱错了", "唱错", "唱砸了", "唱岔了", "唱漏了", "唱串了", "跑调了",
            "这遍不算", "这段不算", "这次不算", "刚才那个不算", "刚才不算",
            "唱得不对", "唱的不对", "歌词唱错", "歌词错了", "词唱错",
            "間違え", "間違った", "歌詞をミス", "今のなし", "今のは無し",
            "sang it wrong", "got it wrong", "messed that up", "scratch that",
            "that take doesn't count", "that one doesn't count",
            "歌えない", "歌えなく", "歌うのをやめ", "歌うのやめ", "歌詞を忘れ",
            "歌詞忘れ", "続きがわから", "続きわから", "ここまでしか",
            "can't sing", "cannot sing", "can't continue", "cannot continue",
            "don't know the rest", "do not know the rest", "forgot the lyrics",
            "forget the lyrics", "that's all i know", "stop singing", "i give up"
        };
        int tailWindow = Mathf.Min(lower.Length, 80);
        int tailStart = lower.Length - tailWindow;
        string tail = lower.Substring(tailStart);
        foreach (string phrase in phrases)
        {
            int index = lower.LastIndexOf(phrase, StringComparison.Ordinal);
            if (index < tailStart) continue;
            //「你唱错了」是在说**她**唱错，不是用户作废自己这一遍。命中即丢弃本轮
            //音频，假阳会静默吞掉一段本该回哼的演唱——撤回判定那边量过，
            //假阳正是不能出的方向。所以第二人称打头的一律不算。
            if (IsSecondPersonBlame(lower, index)) continue;
            return true;
        }

        // ASR frequently inserts pronouns or particles inside the same intent, e.g.
        // "后面的歌词我忘了" or "这句我真的唱不上去了". Fixed phrases alone
        // cannot cover those revisions, so combine only strongly related terms in the
        // utterance tail. ChatSample additionally requires singing evidence before this
        // can reclassify a conversation turn.
        bool mentionsSinging = tail.IndexOf("唱", StringComparison.Ordinal) >= 0;
        bool cannotContinueSinging =
            tail.IndexOf("不会", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不下", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不上", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不了", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不来", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不出", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不动", StringComparison.Ordinal) >= 0;
        if (mentionsSinging && cannotContinueSinging) return true;

        bool mentionsRemainder =
            tail.IndexOf("歌词", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("后面", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("接下来", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("下一句", StringComparison.Ordinal) >= 0;
        bool doesNotKnowRemainder =
            tail.IndexOf("忘", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不记得", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不知道", StringComparison.Ordinal) >= 0 ||
            tail.IndexOf("不会", StringComparison.Ordinal) >= 0;
        if (mentionsRemainder && doesNotKnowRemainder) return true;
        return false;
    }

    /// <summary>
    /// Reclassifies the current result for downstream conversation. If this response
    /// was already cached as a performance, restore the preceding valid performance.
    /// </summary>
    public void DowngradeLastSingingToSpeech(string reason)
    {
        if (!LastIsSinging) return;
        LastIsSinging = false;
        if (m_LastSingingCacheSerial == m_LastCompletedAsrSerial)
        {
            m_LastSingingAudioBytes = m_RollbackSingingAudioBytes;
            m_LastSingingLyrics = m_RollbackSingingLyrics;
            m_LastSingingAudioTime = m_RollbackSingingAudioTime;
            m_LastSingingPerformanceTime = m_RollbackSingingPerformanceTime;
            m_LastSingingPerformanceMidi = m_RollbackSingingPerformanceMidi ??
                new float[0];
            m_LastSingingPerformanceFrameSeconds =
                m_RollbackSingingPerformanceFrameSeconds;
            m_LastSingingPerformanceLanguage =
                m_RollbackSingingPerformanceLanguage ?? "";
            m_LastSingingPerformanceScore =
                m_RollbackSingingPerformanceScore;
            m_LastSingingCacheSerial = -1;
            Debug.Log("[SenseVoice/Singing] 已回滚本轮误写入的歌声缓存，恢复上一段有效演唱");
        }
        LastSingingSummary = "singing classification rejected as speech: " +
            (reason ?? "speech evidence");
        Debug.Log("[SenseVoice/Singing] 本轮歌唱分类已降级为普通说话，不作为可回唱歌声: " +
                  (reason ?? "speech evidence"));
    }

    /// <summary>
    /// Compatibility wrapper for the explicit “singing then spoken tail” safety path.
    /// </summary>
    public void DowngradeLastMixedSingingToSpeech(string reason)
    {
        DowngradeLastSingingToSpeech("mixed singing-to-speech: " + (reason ?? "tail speech"));
    }

    /// <summary>
    /// 算出本轮裁剪后真正会被缓存的那段旋律，不改任何状态。返回 null 表示这一轮
    /// 没有可演奏的时间线。<paramref name="croppedWindowUnplayable"/> 为真时裁出来的
    /// 窗口全是休止，已退回整段，乐谱的裁尾也必须跟着作废。
    /// </summary>
    private float[] ResolvePerformanceTimeline(
        float timelineCropSeconds,
        float timelineEndSeconds,
        out int timelineStart,
        out int timelineEnd,
        out bool croppedWindowUnplayable)
    {
        timelineStart = 0;
        timelineEnd = 0;
        croppedWindowUnplayable = false;
        if (!HasPlayablePitchTimeline(LastPitchTimelineMidi)) return null;

        float frameSeconds = Mathf.Max(0.02f, LastPitchTimelineFrameSeconds);
        timelineStart = Mathf.Clamp(
            Mathf.FloorToInt(timelineCropSeconds / frameSeconds),
            0,
            Mathf.Max(0, LastPitchTimelineMidi.Length - 1));
        //裁尾：唱完之后接的那段说话如果留在时间线里，回哼会把它当成歌词一起唱出来。
        timelineEnd = LastPitchTimelineMidi.Length;
        if (timelineEndSeconds > 0.001f)
        {
            timelineEnd = Mathf.Clamp(
                Mathf.CeilToInt(timelineEndSeconds / frameSeconds),
                timelineStart,
                LastPitchTimelineMidi.Length);
        }
        int timelineLength = timelineEnd - timelineStart;
        //素材太短就退回整段——宁可多唱一点，也不要没得唱
        if (timelineLength < 4) { timelineEnd = LastPitchTimelineMidi.Length; timelineLength = timelineEnd - timelineStart; }
        var performance = new float[timelineLength];
        Array.Copy(
            LastPitchTimelineMidi,
            timelineStart,
            performance,
            0,
            timelineLength);
        if (!HasPlayablePitchTimeline(performance))
        {
            performance = new float[LastPitchTimelineMidi.Length];
            Array.Copy(
                LastPitchTimelineMidi,
                performance,
                performance.Length);
            timelineStart = 0;
            timelineEnd = LastPitchTimelineMidi.Length;
            croppedWindowUnplayable = true;
        }
        return performance;
    }

    /// <summary>
    /// 这一轮按给定裁剪之后还剩多少可唱素材(秒)。没有可演奏时间线时返回 0。
    /// </summary>
    private float MeasurePerformanceSeconds(
        float timelineCropSeconds,
        float timelineEndSeconds)
    {
        int start;
        int end;
        bool fellBack;
        float[] performance = ResolvePerformanceTimeline(
            timelineCropSeconds, timelineEndSeconds, out start, out end, out fellBack);
        if (performance == null) return 0f;
        return performance.Length * Mathf.Max(0.02f, LastPitchTimelineFrameSeconds);
    }

    private void CacheLastSingingPerformance(
        byte[] audioBytes,
        float audioCropSeconds = 0f,
        float timelineCropSeconds = 0f,
        float scoreCropSeconds = 0f,
        float audioEndSeconds = 0f,
        float timelineEndSeconds = 0f,
        float scoreEndSeconds = 0f)
    {
        //长度检查必须赶在任何赋值之前：音频和旋律要么一起换，要么一起不换，
        //否则旧时间线会配上新音频。
        int timelineStart;
        int timelineEnd;
        bool croppedWindowUnplayable;
        float[] performance = ResolvePerformanceTimeline(
            timelineCropSeconds,
            timelineEndSeconds,
            out timelineStart,
            out timelineEnd,
            out croppedWindowUnplayable);
        float frameSeconds = Mathf.Max(0.02f, LastPitchTimelineFrameSeconds);
        if (performance != null &&
            performance.Length * frameSeconds < k_MinSingablePerformanceSeconds)
        {
            Debug.Log("[SenseVoice/Singing] 本轮可唱素材只有 " +
                      $"{performance.Length * frameSeconds:F2}s（{performance.Length} 帧），" +
                      $"短于 {k_MinSingablePerformanceSeconds:F1}s，不作为可回唱歌声，" +
                      "沿用上一段。");
            return;
        }

        m_RollbackSingingAudioBytes = m_LastSingingAudioBytes;
        m_RollbackSingingLyrics = m_LastSingingLyrics;
        m_RollbackSingingAudioTime = m_LastSingingAudioTime;
        m_RollbackSingingPerformanceTime = m_LastSingingPerformanceTime;
        m_RollbackSingingPerformanceMidi = m_LastSingingPerformanceMidi;
        m_RollbackSingingPerformanceFrameSeconds =
            m_LastSingingPerformanceFrameSeconds;
        m_RollbackSingingPerformanceLanguage =
            m_LastSingingPerformanceLanguage;
        m_RollbackSingingPerformanceScore =
            m_LastSingingPerformanceScore;
        m_LastSingingCacheSerial = m_LastCompletedAsrSerial;

        float now = Time.realtimeSinceStartup;
        //歌词必须和裁过的旋律来自同一段音频，否则 SVS 会把整轮的字铺到几秒的旋律上。
        bool usedSegmentLyrics = !string.IsNullOrWhiteSpace(m_LastResponseSingingText);
        m_LastSingingLyrics = usedSegmentLyrics
            ? m_LastResponseSingingText.Trim()
            : (LastText ?? "");
        float actualAudioCrop = 0f;
        float actualAudioEnd = 0f;
        if (audioBytes != null && audioBytes.Length > 44)
        {
            m_LastSingingAudioBytes = TrimWavWindow(
                audioBytes,
                audioCropSeconds,
                audioEndSeconds,
                out actualAudioCrop,
                out actualAudioEnd);
            m_LastSingingAudioTime = now;
        }
        if (performance == null) return;

        m_LastSingingPerformanceMidi = performance;
        if (croppedWindowUnplayable) scoreEndSeconds = 0f;
        m_LastSingingPerformanceFrameSeconds = LastPitchTimelineFrameSeconds;
        //语言必须跟着歌词走：整轮可能判 zh，而唱的那一段是日文。8/8 实测标签用了
        //整轮的 zh，9883 拿中文 G2P 撞上假名，整份乐谱被弃用退化成 backend-transcription，
        //旋律对而歌词全是它自己瞎猜的。
        m_LastSingingPerformanceLanguage = usedSegmentLyrics &&
            !string.IsNullOrWhiteSpace(m_LastResponseSingingLanguage)
            ? m_LastResponseSingingLanguage
            : (LastLanguage ?? "");
        m_LastSingingPerformanceScore =
            CropSingingScore(LastSingingScore, scoreCropSeconds, scoreEndSeconds);
        //乐谱自带一份 lyrics，裁剪不会动它。9883 走 acoustic_voiced_time 对齐，
        //整轮的字铺到裁短的音符上就成了一串听不清的音——必须换成同段的歌词。
        if (usedSegmentLyrics && m_LastSingingPerformanceScore != null)
        {
            m_LastSingingPerformanceScore.lyrics = m_LastSingingLyrics;
            m_LastSingingPerformanceScore.lyrics_alignment = "singing-segment-asr";
            m_LastSingingPerformanceScore.language = m_LastSingingPerformanceLanguage;
            //假名/摩拉由服务端连同分段歌词一起产出。9883 明确要求 SenseVoice 提供
            //lyrics_reading，它自己不会生成——清空会直接吃 422。
            m_LastSingingPerformanceScore.lyrics_reading = m_LastResponseSingingReading;
            m_LastSingingPerformanceScore.lyrics_reading_source =
                m_LastResponseSingingReadingSource;
            m_LastSingingPerformanceScore.lyrics_reading_complete =
                m_LastResponseSingingReadingComplete;
            m_LastSingingPerformanceScore.lyrics_mora = m_LastResponseSingingMora;
        }
        m_LastSingingPerformanceTime = now;
        bool trimmedTail = actualAudioEnd > 0.001f ||
            timelineEnd < LastPitchTimelineMidi.Length;
        if (actualAudioCrop >= 0.05f || timelineStart > 0 || trimmedTail)
        {
            Debug.Log($"[SenseVoice/Singing] cached performance trim " +
                      $"head(audio={actualAudioCrop:F2}s timeline={timelineStart * frameSeconds:F2}s) " +
                      $"tail(audio={actualAudioEnd:F2}s timeline={timelineEnd * frameSeconds:F2}s/" +
                      $"{LastPitchTimelineMidi.Length * frameSeconds:F2}s) " +
                      $"frames={m_LastSingingPerformanceMidi.Length} " +
                      $"lyrics={(usedSegmentLyrics ? "segment" : "full-turn")}" +
                      $"({m_LastSingingLyrics.Length}字, lang={m_LastSingingPerformanceLanguage}" +
                      $"{(usedSegmentLyrics && LastLanguage != m_LastSingingPerformanceLanguage ? $"←整轮{LastLanguage}" : "")}" +
                      $", kana={m_LastResponseSingingReadingComplete})");
        }
    }

    private static float GetWavDurationSeconds(byte[] wavBytes)
    {
        if (wavBytes == null || wavBytes.Length <= 44 ||
            wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I' ||
            wavBytes[2] != (byte)'F' || wavBytes[3] != (byte)'F' ||
            wavBytes[8] != (byte)'W' || wavBytes[9] != (byte)'A' ||
            wavBytes[10] != (byte)'V' || wavBytes[11] != (byte)'E')
            return 0f;

        int byteRate = 0;
        int dataLength = 0;
        int cursor = 12;
        while (cursor + 8 <= wavBytes.Length)
        {
            int chunkLength = BitConverter.ToInt32(wavBytes, cursor + 4);
            if (chunkLength < 0) return 0f;
            int chunkData = cursor + 8;
            if (chunkData > wavBytes.Length || chunkLength > wavBytes.Length - chunkData)
                return 0f;

            bool isFormat = wavBytes[cursor] == (byte)'f' &&
                wavBytes[cursor + 1] == (byte)'m' &&
                wavBytes[cursor + 2] == (byte)'t' &&
                wavBytes[cursor + 3] == (byte)' ';
            bool isData = wavBytes[cursor] == (byte)'d' &&
                wavBytes[cursor + 1] == (byte)'a' &&
                wavBytes[cursor + 2] == (byte)'t' &&
                wavBytes[cursor + 3] == (byte)'a';
            if (isFormat && chunkLength >= 16)
                byteRate = BitConverter.ToInt32(wavBytes, chunkData + 8);
            if (isData)
            {
                dataLength = chunkLength;
                break;
            }
            cursor = chunkData + chunkLength + (chunkLength & 1);
        }
        return byteRate > 0 && dataLength > 0 ? dataLength / (float)byteRate : 0f;
    }

    /// <summary>
    /// 把乐谱裁到 [cropSeconds, endSeconds)。endSeconds &lt;= 0 表示保留到末尾。
    /// </summary>
    private static SingingScore CropSingingScore(
        SingingScore source,
        float cropSeconds,
        float endSeconds = 0f)
    {
        if (source == null || source.schema_version <= 0) return null;
        SingingScore score = JsonUtility.FromJson<SingingScore>(
            JsonUtility.ToJson(source));
        if (score == null || (cropSeconds <= 0.001f && endSeconds <= 0.001f))
            return score;

        bool hasTail = endSeconds > 0.001f;
        float tail = hasTail ? endSeconds : float.MaxValue;
        float frameSeconds = Mathf.Max(0.001f, score.frame_seconds);
        int frameOffset = Mathf.Max(0, Mathf.FloorToInt(cropSeconds / frameSeconds));
        int frameEnd = hasTail ? Mathf.CeilToInt(endSeconds / frameSeconds) : -1;
        score.f0_hz = SliceFloatArray(score.f0_hz, frameOffset, frameEnd);
        score.energy = SliceFloatArray(score.energy, frameOffset, frameEnd);
        score.duration_seconds = Mathf.Max(
            0f, Mathf.Min(score.duration_seconds, tail) - cropSeconds);

        var notes = new List<SingingNote>();
        if (score.notes != null)
        {
            foreach (SingingNote note in score.notes)
            {
                if (note == null) continue;
                float originalStart = note.start_seconds;
                float originalEnd = originalStart + Mathf.Max(0f, note.duration_seconds);
                if (originalEnd <= cropSeconds || originalStart >= tail) continue;
                float clippedStart = Mathf.Max(originalStart, cropSeconds);
                float clippedEnd = Mathf.Min(originalEnd, tail);
                note.start_seconds = clippedStart - cropSeconds;
                note.duration_seconds = Mathf.Max(0f, clippedEnd - clippedStart);
                if (note.duration_seconds > 0.001f) notes.Add(note);
            }
        }
        score.notes = notes.ToArray();

        var breaths = new List<float>();
        if (score.breath_positions_seconds != null)
        {
            foreach (float breath in score.breath_positions_seconds)
            {
                if (breath >= cropSeconds && breath < tail)
                    breaths.Add(breath - cropSeconds);
            }
        }
        score.breath_positions_seconds = breaths.ToArray();

        var vibrato = new List<SingingVibrato>();
        if (score.vibrato != null)
        {
            foreach (SingingVibrato region in score.vibrato)
            {
                if (region == null) continue;
                float originalStart = region.start_seconds;
                float originalEnd = originalStart + Mathf.Max(0f, region.duration_seconds);
                if (originalEnd <= cropSeconds || originalStart >= tail) continue;
                float clippedStart = Mathf.Max(originalStart, cropSeconds);
                float clippedEnd = Mathf.Min(originalEnd, tail);
                region.start_seconds = clippedStart - cropSeconds;
                region.duration_seconds = Mathf.Max(0f, clippedEnd - clippedStart);
                if (region.duration_seconds > 0.001f) vibrato.Add(region);
            }
        }
        score.vibrato = vibrato.ToArray();
        return score;
    }

    /// <summary>endExclusive &lt; 0 表示切到末尾。</summary>
    private static float[] SliceFloatArray(float[] source, int start, int endExclusive = -1)
    {
        if (source == null || source.Length == 0 || start >= source.Length)
            return new float[0];
        start = Mathf.Max(0, start);
        int end = endExclusive < 0
            ? source.Length
            : Mathf.Clamp(endExclusive, start, source.Length);
        if (end <= start) return new float[0];
        float[] result = new float[end - start];
        Array.Copy(source, start, result, 0, result.Length);
        return result;
    }

    /// <summary>
    /// 把 WAV 裁到 [startSeconds, endSeconds) 这一段。
    /// endSeconds &lt;= 0 表示"没有可信的结束位置"，保留到末尾。
    /// </summary>
    private static byte[] TrimWavWindow(
        byte[] wavBytes,
        float startSeconds,
        float endSeconds,
        out float actualStartSeconds,
        out float actualEndSeconds)
    {
        actualStartSeconds = 0f;
        actualEndSeconds = 0f;
        if (wavBytes == null || wavBytes.Length <= 44 ||
            (startSeconds < 0.05f && endSeconds <= 0f))
            return wavBytes;
        if (wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I' ||
            wavBytes[2] != (byte)'F' || wavBytes[3] != (byte)'F' ||
            wavBytes[8] != (byte)'W' || wavBytes[9] != (byte)'A' ||
            wavBytes[10] != (byte)'V' || wavBytes[11] != (byte)'E')
            return wavBytes;

        int formatOffset = -1;
        int dataHeaderOffset = -1;
        int dataOffset = -1;
        int dataLength = 0;
        int cursor = 12;
        while (cursor + 8 <= wavBytes.Length)
        {
            int chunkLength = BitConverter.ToInt32(wavBytes, cursor + 4);
            if (chunkLength < 0) return wavBytes;
            int chunkData = cursor + 8;
            if (chunkData > wavBytes.Length || chunkLength > wavBytes.Length - chunkData)
                return wavBytes;

            bool isFormat = wavBytes[cursor] == (byte)'f' &&
                wavBytes[cursor + 1] == (byte)'m' &&
                wavBytes[cursor + 2] == (byte)'t' &&
                wavBytes[cursor + 3] == (byte)' ';
            bool isData = wavBytes[cursor] == (byte)'d' &&
                wavBytes[cursor + 1] == (byte)'a' &&
                wavBytes[cursor + 2] == (byte)'t' &&
                wavBytes[cursor + 3] == (byte)'a';
            if (isFormat && chunkLength >= 16) formatOffset = chunkData;
            if (isData)
            {
                dataHeaderOffset = cursor;
                dataOffset = chunkData;
                dataLength = chunkLength;
                break;
            }
            cursor = chunkData + chunkLength + (chunkLength & 1);
        }

        if (formatOffset < 0 || dataHeaderOffset < 0 || dataOffset < 0 ||
            formatOffset + 16 > wavBytes.Length)
            return wavBytes;
        int byteRate = BitConverter.ToInt32(wavBytes, formatOffset + 8);
        int blockAlign = BitConverter.ToInt16(wavBytes, formatOffset + 12);
        if (byteRate <= 0 || blockAlign <= 0) return wavBytes;

        int skipBytes = Mathf.FloorToInt(startSeconds * byteRate);
        skipBytes -= skipBytes % blockAlign;
        skipBytes = Mathf.Clamp(skipBytes, 0, dataLength);
        int remaining = dataLength - skipBytes;

        //裁尾：唱完之后接的那段说话必须去掉，否则会被当成歌词唱回去。
        //endSeconds <= 0 表示"没有可信的结束位置"，此时保留到末尾(旧行为)。
        if (endSeconds > 0f)
        {
            int endByte = Mathf.FloorToInt(endSeconds * byteRate);
            endByte -= endByte % blockAlign;
            endByte = Mathf.Clamp(endByte, 0, dataLength);
            int windowed = endByte - skipBytes;
            //尾巴裁完至少要留 0.35s，否则宁可不裁——素材太短反而没法回哼
            if (windowed >= Mathf.CeilToInt(byteRate * 0.35f) && windowed < remaining)
            {
                remaining = windowed;
                actualEndSeconds = endByte / (float)byteRate;
            }
        }
        if ((skipBytes <= 0 && actualEndSeconds <= 0f) ||
            remaining < Mathf.CeilToInt(byteRate * 0.35f))
        {
            actualEndSeconds = 0f;
            return wavBytes;
        }

        byte[] trimmed = new byte[dataOffset + remaining];
        Array.Copy(wavBytes, 0, trimmed, 0, dataOffset);
        Array.Copy(wavBytes, dataOffset + skipBytes, trimmed, dataOffset, remaining);
        Array.Copy(BitConverter.GetBytes(remaining), 0, trimmed, dataHeaderOffset + 4, 4);
        Array.Copy(BitConverter.GetBytes(trimmed.Length - 8), 0, trimmed, 4, 4);
        actualStartSeconds = skipBytes / (float)byteRate;
        return trimmed;
    }

    private static bool HasPlayablePitchTimeline(float[] timeline)
    {
        if (timeline == null || timeline.Length < 4) return false;
        int voiced = 0;
        for (int i = 0; i < timeline.Length; i++)
        {
            float value = timeline[i];
            if (value > 1f && !float.IsNaN(value) && !float.IsInfinity(value)) voiced++;
        }
        return voiced >= 4;
    }

    /// <summary>
    /// 给设置页或未来的管理 UI 使用。临时访客改名后，晋升永久档案时会沿用此名称。
    /// </summary>
    public void RenameSpeaker(string speakerId, string displayName, Action<bool> callback)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(displayName))
        {
            if (callback != null) callback(false);
            return;
        }
        StartCoroutine(RenameSpeakerRequest(speakerId, displayName.Trim(), callback));
    }

    public void ResetOwnerEnrollment(Action<bool> callback)
    {
        StartCoroutine(SimpleSpeakerPost("/speakers/reset-owner", new WWWForm(), callback));
    }

    private IEnumerator RenameSpeakerRequest(string speakerId, string displayName, Action<bool> callback)
    {
        WWWForm form = new WWWForm();
        form.AddField("speaker_id", speakerId);
        form.AddField("display_name", displayName);
        yield return SimpleSpeakerPost("/speakers/rename", form, callback);
    }

    private IEnumerator SimpleSpeakerPost(string route, WWWForm form, Action<bool> callback)
    {
        using (UnityWebRequest www = UnityWebRequest.Post(m_ServerSetting.TrimEnd('/') + route, form))
        {
            yield return www.SendWebRequest();
            bool ok = www.result == UnityWebRequest.Result.Success;
            if (!ok) Debug.LogWarning("[Speaker] 管理请求失败: " + www.downloadHandler.text);
            else if (m_VerboseLog) Debug.Log("[Speaker] 管理请求成功: " + route);
            if (callback != null) callback(ok);
        }
    }

    /// <summary>
    /// 由角色的 &lt;song_search/&gt; 工具调用。Unity 只把录音发回本机服务。
    /// </summary>
    public void SearchSong(
        string query,
        string mode,
        string reason,
        Action<SongSearchResult> callback)
    {
        StartCoroutine(SendSongSearch(query, mode, reason, callback));
    }

    private IEnumerator SendSongSearch(
        string query,
        string mode,
        string reason,
        Action<SongSearchResult> callback)
    {
        WWWForm form = new WWWForm();
        form.AddField("query", query ?? "");
        form.AddField("mode", string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant());
        form.AddField("max_results", "5");

        bool hasFreshAudio = m_LastSingingAudioBytes != null &&
            m_LastSingingAudioBytes.Length > 44 &&
            Time.realtimeSinceStartup - m_LastSingingAudioTime <= m_SingingAudioRetentionSeconds;
        if (hasFreshAudio)
            form.AddBinaryData("audio_file", m_LastSingingAudioBytes, "last_singing.wav", "audio/wav");

        if (m_VerboseLog)
        {
            Debug.Log($"[SongSearch] mode={mode} query=\"{query}\" audio={hasFreshAudio} reason={reason}");
        }

        using (UnityWebRequest www = UnityWebRequest.Post(m_SongSearchURL, form))
        {
            www.SetRequestHeader("accept", "application/json");
            yield return www.SendWebRequest();
            if (www.result != UnityWebRequest.Result.Success)
            {
                SongSearchResult failed = new SongSearchResult
                {
                    Ok = false,
                    Error = www.error + " / " + www.downloadHandler.text,
                    Summary = "歌曲检索暂时失败。",
                };
                Debug.LogWarning("[SongSearch] 请求失败: " + failed.Error);
                if (callback != null) callback(failed);
                yield break;
            }

            SongSearchResponse response = null;
            try { response = JsonUtility.FromJson<SongSearchResponse>(www.downloadHandler.text); }
            catch (Exception e) { Debug.LogWarning("[SongSearch] JSON 解析失败: " + e.Message); }

            SongSearchResult result = new SongSearchResult();
            if (response == null)
            {
                result.Ok = false;
                result.Error = "invalid response";
                result.Summary = "歌曲检索没有返回可理解的结果。";
            }
            else
            {
                result.Ok = response.ok;
                result.Reliable = response.reliable;
                result.Query = response.query ?? "";
                result.Mode = response.mode ?? "";
                result.Summary = response.summary ?? "";
                result.Privacy = response.privacy ?? "";
                result.Matches = response.matches ?? new SongMatch[0];
                result.Error = response.error ?? "";
            }
            if (callback != null) callback(result);
        }
    }

    public bool HasFreshSingingAudio()
    {
        return m_LastSingingAudioBytes != null &&
            m_LastSingingAudioBytes.Length > 44 &&
            Time.realtimeSinceStartup - m_LastSingingAudioTime <= m_SingingAudioRetentionSeconds;
    }

    /// <summary>
    /// Returns a private snapshot of the latest real singing/humming WAV.  Neural voice
    /// conversion uses the performance itself so timing, breathing and pitch expression
    /// survive; callers cannot mutate the ASR/song-memory cache.
    /// </summary>
    public bool TryGetRecentSingingAudio(out byte[] wavBytes)
    {
        wavBytes = null;
        if (!HasFreshSingingAudio()) return false;
        wavBytes = new byte[m_LastSingingAudioBytes.Length];
        Array.Copy(m_LastSingingAudioBytes, wavBytes, wavBytes.Length);
        return true;
    }

    /// <summary>
    /// 上一段可回唱演唱距今多少秒；从来没有过则为 -1。
    /// 失败时要把**具体原因**说出来——只说"没有取得可播放的旋律"，
    /// 她会自己编一个（8/24 实测编成了"因为噪音"，真实原因是隔了 246 秒超过保留期）。
    /// </summary>
    public float LastSingingPerformanceAgeSeconds
    {
        get
        {
            if (m_LastSingingPerformanceTime <= 0f) return -1f;
            return Time.realtimeSinceStartup - m_LastSingingPerformanceTime;
        }
    }

    /// <summary>回唱素材的保留期，用于把"为什么没了"讲清楚。</summary>
    public float SingingAudioRetentionSeconds { get { return m_SingingAudioRetentionSeconds; } }

    public int PracticePhraseCount
    {
        get { return m_PracticePhrases.Count; }
    }

    /// <summary>练唱会话里每一段的身份快照，供感知帧列给她看。</summary>
    public List<PracticePhraseInfo> DescribePracticePhrases()
    {
        var list = new List<PracticePhraseInfo>(m_PracticePhrases.Count);
        for (int i = 0; i < m_PracticePhrases.Count; i++)
        {
            list.Add(new PracticePhraseInfo
            {
                Index = i + 1,
                Lyrics = m_PracticePhrases[i].Lyrics ?? "",
                Seconds = m_PracticePhrases[i].Seconds,
                Language = m_PracticePhrases[i].Language ?? "",
                SongId = m_PracticePhrases[i].SongId ?? "",
                SongName = m_PracticePhrases[i].SongName ?? "",
                AgoSeconds = Mathf.Max(
                    0f, Time.realtimeSinceStartup - m_PracticePhrases[i].AtRealtime),
                PitchMedianMidi = MedianVoicedPitch(m_PracticePhrases[i].MidiTimeline),
                PrecedingSpeech = m_PracticePhrases[i].PrecedingSpeech ?? "",
                PendingConfirmation = m_PracticePhrases[i].PendingConfirmation,
            });
        }
        AnnotateTakeGroups(list);
        AnnotatePitchBases(list);
        return list;
    }

    /// <summary>
    /// 标出各段的音高中位，以及相对"共同基准"的偏移。
    ///
    /// 基准取各段中位数的中位数——多数段落定的调就是基准，个别偏低/偏高的那段
    /// 会被显出来。8/20 实测段1/段2 都是 60、段3 是 57，基准 60，段3 报 −3。
    /// 只做展示：要不要对齐、对齐到哪，仍然由用户和她决定。
    /// </summary>
    private static void AnnotatePitchBases(List<PracticePhraseInfo> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            //音名后面带上 MIDI 数值。8/21 实测她照音名做减法连错三次：
            //D#3→D3 她算成 4(实际 1)、G#3→D#3 算成 -4(实际 -5)，三段调完更不齐了。
            //给出整数之后"差几个半音"就是两个数相减，不必在音名上数格子。
            list[i].PitchBaseNote = list[i].PitchMedianMidi > 0f
                ? $"{MidiToNoteName(list[i].PitchMedianMidi)}" +
                  $"({Mathf.RoundToInt(list[i].PitchMedianMidi)})"
                : "";
        }
    }

    private static readonly string[] s_NoteNames =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B",
    };

    /// <summary>
    /// 与服务端 singing_analysis.midi_to_note 保持同一套写法，两边显示的音名才对得上。
    /// </summary>
    public static string MidiToNoteName(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return "";
        int note = Mathf.RoundToInt(value);
        int octave = Mathf.FloorToInt(note / 12f) - 1;
        int index = ((note % 12) + 12) % 12;
        return s_NoteNames[index] + octave;
    }

    /// <summary>有声帧音高的中位数；全是休止时返回 0。</summary>
    private static float MedianVoicedPitch(float[] timeline)
    {
        if (timeline == null || timeline.Length == 0) return 0f;
        var voiced = new List<float>(timeline.Length);
        foreach (float v in timeline) if (v > 1f) voiced.Add(v);
        if (voiced.Count == 0) return 0f;
        voiced.Sort();
        return voiced[voiced.Count / 2];
    }

    /// <summary>
    /// 标出"同一句的第几遍"。判据用歌词——用户重唱同一句时歌词高度一致，
    /// 而不同段落的歌词差别很大。刻意不用旋律相似度：实测它连不同的歌都分不开
    /// (组内中位 0.701 / 跨组 0.675)，拿来分"同一句的两遍"更不可能。
    /// 这里只做展示分组，不影响任何合成行为——选哪一遍仍然由用户/她用 order 决定。
    /// </summary>
    private static void AnnotateTakeGroups(List<PracticePhraseInfo> list)
    {
        int nextGroup = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].TakeGroup > 0) continue;
            nextGroup++;
            list[i].TakeGroup = nextGroup;
            string a = NormalizeLyricForTake(list[i].Lyrics);
            for (int j = i + 1; j < list.Count; j++)
            {
                if (list[j].TakeGroup > 0) continue;
                string b = NormalizeLyricForTake(list[j].Lyrics);
                if (a.Length == 0 || b.Length == 0) continue;
                if (LyricsLooksSamePhrase(a, b)) list[j].TakeGroup = nextGroup;
            }
        }
        for (int g = 1; g <= nextGroup; g++)
        {
            int total = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].TakeGroup == g) total++;
            int seq = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].TakeGroup != g) continue;
                list[i].TakeIndex = ++seq;
                list[i].TakeTotal = total;
            }
        }
    }

    private static string NormalizeLyricForTake(string lyric)
    {
        if (string.IsNullOrEmpty(lyric)) return "";
        var sb = new StringBuilder(lyric.Length);
        foreach (char c in lyric)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    //唱歌 ASR 每一遍的错字都不同，所以不能要求完全相等。0.62 是从 8/17 那一场
    //定的：同一句的两遍(「紧闭双眼才能看得见…」)算出来 0.78，而相邻的不同段落
    //(「说好了它永远断了线…」vs「紧闭双眼…」)只有 0.15，中间隔得很开。
    private static bool LyricsLooksSamePhrase(string a, string b)
    {
        if (a == b) return true;
        int min = Mathf.Min(a.Length, b.Length);
        if (min < 4) return false;
        if (a.Contains(b) || b.Contains(a)) return true;
        return LyricOverlapRatio(a, b) >= 0.62f;
    }

    private static float LyricOverlapRatio(string a, string b)
    {
        //按 2-gram 交并比，比逐字编辑距离更耐 ASR 错字
        var ga = new HashSet<string>();
        for (int i = 0; i + 1 < a.Length; i++) ga.Add(a.Substring(i, 2));
        var gb = new HashSet<string>();
        for (int i = 0; i + 1 < b.Length; i++) gb.Add(b.Substring(i, 2));
        if (ga.Count == 0 || gb.Count == 0) return 0f;
        int inter = 0;
        foreach (string g in ga) if (gb.Contains(g)) inter++;
        return inter / (float)(ga.Count + gb.Count - inter);
    }

    /// <summary>
    /// 把 <c>order="2,1"</c> 解析成 0 起的下标序列。
    /// 空串／解析不出任何合法序号时返回 null，调用方按原顺序走。
    /// 允许只取其中几段，也允许重复(用户可能要求"再唱一遍第一段")。
    /// </summary>
    private List<int> ParsePracticeOrder(string order)
    {
        if (string.IsNullOrWhiteSpace(order)) return null;
        var picked = new List<int>();
        foreach (string piece in order.Split(',', '，', ' ', '、', '-', '>'))
        {
            string t = piece.Trim();
            if (t.Length == 0) continue;
            int n;
            if (!int.TryParse(t, out n)) continue;
            if (n < 1 || n > m_PracticePhrases.Count) continue;
            picked.Add(n - 1);
        }
        return picked.Count > 0 ? picked : null;
    }

    /// <summary>
    /// Starts a new in-memory practice sequence. Persistent song memories are untouched.
    /// </summary>
    public void BeginSingingPracticeSession()
    {
        m_PracticePhrases.Clear();
        m_LastCommittedPracticeSignature = 0;
        m_LastPracticeCommitTime = -999f;
        Debug.Log("[SenseVoice/Practice] 新练唱会话已开始；等待最终确认的歌唱片段");
    }

    /// <summary>
    /// Commits the current authoritative final singing cache exactly once. Streaming
    /// hypotheses never enter this list, so a later correction cannot leave a phantom phrase.
    /// </summary>
    /// <param name="pendingConfirmation">
    /// 软降级来的段落传 true：照常写进会话，但标成"待确认"。
    /// 写入与确认必须分开——8/24 实测把两者绑在一起(不确认就不写)时，
    /// 用户口头确认了、她也回哼了，那一段仍然不在会话里，连唱直接做不到。
    /// </param>
    public bool CommitRecentSingingToPracticeSession(
        out int phraseCount, bool pendingConfirmation = false)
    {
        phraseCount = m_PracticePhrases.Count;
        if (!TryGetRecentSingingAudio(out byte[] wavBytes) ||
            !TryGetRecentSingingPerformance(out float[] timeline, out float frameSeconds,
                out string language))
            return false;

        int signature = ComputePracticeSignature(wavBytes, timeline);
        if (signature == m_LastCommittedPracticeSignature &&
            Time.realtimeSinceStartup - m_LastPracticeCommitTime < 8f)
            return false;

        //本轮回忆的首选候选：只在够像时才当身份线索用。相似度分不开不同的歌
        //(实测组内中位 0.701 / 跨组 0.675)，所以这里要的不是"判定"，而是"提示"——
        //她最终仍然靠歌词自己判断，写错也只是少一条线索。
        string topRecallId = "";
        string topRecallName = "";
        if (m_LastSongRecall != null)
        {
            foreach (var item in m_LastSongRecall)
            {
                if (item == null || string.IsNullOrEmpty(item.song_id)) continue;
                topRecallId = item.song_id;
                topRecallName = item.named ? (item.display_name ?? "").Trim() : "";
                break;
            }
        }

        if (m_PracticePhrases.Count >= MaxPracticePhraseCount)
            m_PracticePhrases.RemoveAt(0);
        m_PracticePhrases.Add(new PracticePhrase
        {
            WavBytes = wavBytes,
            MidiTimeline = timeline,
            FrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f),
            Language = language ?? "",
            Signature = signature,
            //分段歌词比整轮文本更贴近真正唱的那一段
            Lyrics = !string.IsNullOrWhiteSpace(m_LastSingingLyrics)
                ? m_LastSingingLyrics.Trim()
                : (LastText ?? "").Trim(),
            Seconds = GetWavDurationSeconds(wavBytes),
            //本轮曲库回忆的首选就是现成的身份线索，不额外算
            SongId = topRecallId,
            SongName = topRecallName,
            AtRealtime = Time.realtimeSinceStartup,
            PrecedingSpeech = m_LastSpokenTranscript ?? "",
            PendingConfirmation = pendingConfirmation,
        });
        m_LastCommittedPracticeSignature = signature;
        m_LastPracticeCommitTime = Time.realtimeSinceStartup;
        phraseCount = m_PracticePhrases.Count;
        Debug.Log($"[SenseVoice/Practice] 最终歌声已提交 sequence={phraseCount} " +
                  $"audio={GetWavDurationSeconds(wavBytes):F2}s frames={timeline.Length}" +
                  (pendingConfirmation ? " (待确认)" : ""));
        return true;
    }

    /// <summary>练唱会话里还标着"待确认"的段号(1 起)。</summary>
    public List<int> PendingConfirmationPracticeIndices()
    {
        var list = new List<int>();
        for (int i = 0; i < m_PracticePhrases.Count; i++)
            if (m_PracticePhrases[i].PendingConfirmation) list.Add(i + 1);
        return list;
    }

    /// <summary>
    /// 清掉这几段的"待确认"标。段号 1 起；传空表示清掉全部待确认。
    /// </summary>
    /// <remarks>
    /// 确认的形式不是用户嘴上答"是"，而是**这一段真的被唱出去、用户没有异议**：
    /// 那一刻他听到的是实际音频，比任何文字确认都硬。所以调用点在回哼/连唱播完之处。
    /// </remarks>
    public int ConfirmPracticePhrases(List<int> indices1Based)
    {
        int cleared = 0;
        for (int i = 0; i < m_PracticePhrases.Count; i++)
        {
            if (!m_PracticePhrases[i].PendingConfirmation) continue;
            if (indices1Based != null && indices1Based.Count > 0 &&
                !indices1Based.Contains(i + 1)) continue;
            m_PracticePhrases[i].PendingConfirmation = false;
            cleared++;
        }
        if (cleared > 0)
            Debug.Log($"[SenseVoice/Practice] {cleared} 段已确认为歌声，去掉待确认标");
        return cleared;
    }

    /// <summary>
    /// 从练唱会话里去掉一段。段号 1 起，删除后其后各段的段号会前移。
    /// </summary>
    /// <remarks>
    /// 长期曲库那一层早有 song_forget，练唱会话这一层却一直没有对称物：
    /// 只能整场 Clear() 或者到上限时挤掉最老的。于是一段被误收进来的说话
    /// (或者用户唱错想撤回的一遍)只能一直挂在清单里，还会被默认顺序唱出去。
    /// </remarks>
    public bool DropPracticePhrase(
        int index1Based, out string dropped, out int remaining, out string failure)
    {
        dropped = "";
        failure = "";
        remaining = m_PracticePhrases.Count;
        if (m_PracticePhrases.Count == 0)
        {
            failure = "练唱会话里现在一段也没有，没有可以去掉的段落";
            return false;
        }
        if (index1Based < 1 || index1Based > m_PracticePhrases.Count)
        {
            failure = $"没有第 {index1Based} 段：练唱会话现在只有 " +
                      $"{m_PracticePhrases.Count} 段(段号 1~{m_PracticePhrases.Count})";
            return false;
        }
        var phrase = m_PracticePhrases[index1Based - 1];
        string lyric = (phrase.Lyrics ?? "").Trim();
        if (lyric.Length > 20) lyric = lyric.Substring(0, 20) + "…";
        dropped = (lyric.Length > 0 ? "\"" + lyric + "\"" : "（无歌词）") +
                  $" {phrase.Seconds:F1}s";
        m_PracticePhrases.RemoveAt(index1Based - 1);
        remaining = m_PracticePhrases.Count;
        //删掉之后同一段音频要允许重新收进来，否则用户"撤回再唱一次"会被去重挡住。
        m_LastCommittedPracticeSignature = 0;
        Debug.Log($"[SenseVoice/Practice] 已去掉第 {index1Based} 段 {dropped}；" +
                  $"剩余 {remaining} 段，段号已前移");
        return true;
    }

    /// <summary>
    /// Creates a private performance variant of the latest phrase. The melody is not
    /// rewritten: only sub-percent pacing and a slow dynamics contour change between takes.
    /// </summary>
    /// <summary>
    /// paceOverride 为 NaN 时按 seed 取一个近乎不可察的速度扰动；她显式指定时听她的。
    /// </summary>
    public bool TryGetVariedRecentSingingAudio(
        int performanceSeed,
        out byte[] wavBytes,
        out string diagnostic,
        float paceOverride = float.NaN)
    {
        wavBytes = null;
        diagnostic = "";
        if (!TryGetRecentSingingAudio(out byte[] source) ||
            !TryDecodePcmWav(source, out float[] samples, out int sampleRate))
            return false;

        System.Random random = new System.Random(performanceSeed);
        float pace = float.IsNaN(paceOverride)
            ? 0.994f + (float)random.NextDouble() * 0.012f
            : Mathf.Clamp(paceOverride, 0.8f, 1.25f);
        float gainStart = 0.96f + (float)random.NextDouble() * 0.07f;
        float gainEnd = 0.96f + (float)random.NextDouble() * 0.07f;
        samples = ResampleForPace(samples, pace);
        ApplyPerformanceEnvelope(samples, sampleRate, gainStart, gainEnd);
        wavBytes = EncodeMonoPcm16Wav(samples, sampleRate);
        diagnostic = $"seed={performanceSeed}, pace={pace:F3}, dynamics={gainStart:F2}->{gainEnd:F2}";
        return wavBytes != null && wavBytes.Length > 44;
    }

    /// <summary>
    /// Concatenates the confirmed practice sequence into one source WAV before voice
    /// conversion. Phrase order and every phrase's complete start/end are preserved.
    /// Small take-level timing and dynamics differences make repetitions feel performed,
    /// while the same seed keeps one rendition internally coherent.
    /// </summary>
    /// <param name="order">
    /// 演唱顺序，形如 <c>"2,1"</c>；空串按练唱先后。8/16 实测用户连着七轮要求"反过来唱"，
    /// 而这个工具当时只能原序合成、还每次都回报"成功"，她于是反复承诺、反复道歉、
    /// 四次回哼全是同一个结果。顺序必须是可指定的——用户是按内容指段的
    /// (「先唱沉默着走了那段」)，由她照着感知帧里的段号翻译成这个参数。
    /// </param>
    public bool TryBuildSingingPracticeComposition(
        int performanceSeed,
        float maxSeconds,
        out PracticeComposition composition,
        out string failure,
        string order = "")
    {
        composition = null;
        failure = "";
        if (m_PracticePhrases.Count == 0)
        {
            failure = "练唱会话里还没有任何片段，没有可以唱的东西";
            return false;
        }

        List<int> sequence = ParsePracticeOrder(order);
        //原来这里卡的是**库存量** < 2。于是会话里只有一段时，就算用户指名要那一段
        //也一律拒绝——而三段的会话里 order="3" 唱单段却是正常工作的，同样是唱一段，
        //两种结果。真正该卡的是"这次请求解析出来的序列是不是空的"。
        if (sequence == null && !string.IsNullOrWhiteSpace(order))
        {
            //给了 order 却一个合法段号都没有：原来会静默退回"全唱"，
            //她写错段号时用户听到的是一整串，而工具结果还报成功。
            failure = $"order=\"{order.Trim()}\" 里没有任何有效段号：" +
                      $"练唱会话现在有 {m_PracticePhrases.Count} 段" +
                      $"(段号 1~{m_PracticePhrases.Count})，请照感知帧里的清单重填";
            return false;
        }
        if (sequence == null)
        {
            sequence = new List<int>(m_PracticePhrases.Count);
            for (int i = 0; i < m_PracticePhrases.Count; i++) sequence.Add(i);
        }
        if (sequence.Count == 0)
        {
            failure = "这次请求没有解析出任何要唱的段落";
            return false;
        }

        var decoded = new List<float[]>(sequence.Count);
        int outputRate = 0;
        for (int k = 0; k < sequence.Count; k++)
        {
            int i = sequence[k];
            if (!TryDecodePcmWav(
                    m_PracticePhrases[i].WavBytes,
                    out float[] phraseSamples,
                    out int phraseRate))
            {
                failure = $"第 {i + 1} 段不是可组合的 PCM WAV";
                return false;
            }
            if (outputRate <= 0) outputRate = phraseRate;
            if (phraseRate != outputRate)
                phraseSamples = ResampleToRate(phraseSamples, phraseRate, outputRate);
            decoded.Add(phraseSamples);
        }

        System.Random random = new System.Random(performanceSeed);
        const float outputFrameSeconds = 0.10f;
        var output = new List<float>();
        var midi = new List<float>();
        var variation = new StringBuilder();
        string language = "";
        //逐段送转换时用的那一份：内容与拼接进 output 的完全相同(同一个 phrase 数组)，
        //只是没有被拼起来。两条路走同一批采样，听感才不会分叉。
        var segmentWavs = new List<byte[]>(decoded.Count);
        var gaps = new List<float>(decoded.Count);
        var segmentMedians = new List<float>(decoded.Count);

        for (int i = 0; i < decoded.Count; i++)
        {
            // Human takes do not land on sample-identical timing. Keep the change below
            // one percent so phrasing varies without noticeably rewriting the melody.
            float pace = 0.992f + (float)random.NextDouble() * 0.016f;
            float gainStart = 0.94f + (float)random.NextDouble() * 0.10f;
            float gainEnd = 0.94f + (float)random.NextDouble() * 0.10f;
            float[] phrase = ResampleForPace(decoded[i], pace);
            ApplyPerformanceEnvelope(phrase, outputRate, gainStart, gainEnd);
            ApplyShortEdgeFade(phrase, outputRate, 0.012f);

            if (i > 0)
            {
                // A bounded breath-sized pause prevents hard joins and changes naturally
                // from take to take; no phrase audio is overlapped or discarded.
                float gapSeconds = 0.09f + (float)random.NextDouble() * 0.18f;
                int gapSamples = Mathf.RoundToInt(gapSeconds * outputRate);
                for (int s = 0; s < gapSamples; s++) output.Add(0f);
                int gapFrames = Mathf.Max(1, Mathf.RoundToInt(gapSeconds / outputFrameSeconds));
                for (int f = 0; f < gapFrames; f++) midi.Add(0f);
                variation.Append($" gap{i}={gapSeconds:F2}s");
                gaps.Add(gapSeconds);
            }
            else gaps.Add(0f);
            segmentWavs.Add(EncodeMonoPcm16Wav(phrase, outputRate));
            segmentMedians.Add(MedianVoicedPitch(m_PracticePhrases[sequence[i]].MidiTimeline));

            int src = sequence[i];
            output.AddRange(phrase);
            AppendResampledTimeline(
                midi,
                m_PracticePhrases[src].MidiTimeline,
                m_PracticePhrases[src].FrameSeconds,
                outputFrameSeconds,
                pace);
            if (string.IsNullOrEmpty(language) &&
                !string.IsNullOrEmpty(m_PracticePhrases[src].Language))
                language = m_PracticePhrases[src].Language;
            variation.Append($" p{src + 1}={pace:F3}/{gainStart:F2}->{gainEnd:F2}");
        }

        float duration = output.Count / (float)Mathf.Max(1, outputRate);
        if (duration > maxSeconds + 0.02f)
        {
            failure = $"连续演唱需要 {duration:F1}s，超过当前完整转换上限 {maxSeconds:F1}s；没有裁掉开头";
            return false;
        }
        float[] outputSamples = output.ToArray();
        ApplyShortEdgeFade(outputSamples, outputRate, 0.018f);
        DumpPracticeAudio(sequence, outputSamples, outputRate);
        var playedIndices = new List<int>(sequence.Count);
        foreach (int idx in sequence) playedIndices.Add(idx + 1);
        composition = new PracticeComposition
        {
            PlayedIndices = playedIndices,
            WavBytes = EncodeMonoPcm16Wav(outputSamples, outputRate),
            MidiTimeline = midi.ToArray(),
            FrameSeconds = outputFrameSeconds,
            Language = language,
            PhraseCount = m_PracticePhrases.Count,
            DurationSeconds = duration,
            VariationDiagnostic = $"seed={performanceSeed};{variation.ToString().Trim()}",
            SegmentWavs = segmentWavs,
            Gaps = gaps,
            SegmentMedians = segmentMedians,
            MedianMidi = MedianVoicedPitch(midi.ToArray()),
        };
        return composition.WavBytes != null && composition.WavBytes.Length > 44 &&
            HasPlayablePitchTimeline(composition.MidiTimeline);
    }

    /// <summary>
    /// 把这一次参与合成的各段原始音频与拼好的合成源写到磁盘。纯诊断，失败不影响演唱。
    /// </summary>
    private void DumpPracticeAudio(List<int> sequence, float[] composed, int rate)
    {
        if (!m_DumpPracticeAudio) return;
        try
        {
            string dir = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Server", "SenseVoice", "practice_dumps"));
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            for (int k = 0; k < sequence.Count; k++)
            {
                int i = sequence[k];
                if (m_PracticePhrases[i].WavBytes == null) continue;
                //文件名带上段序与歌词开头，离线侧不必再去翻日志对号入座。
                string lyric = (m_PracticePhrases[i].Lyrics ?? "").Trim();
                if (lyric.Length > 8) lyric = lyric.Substring(0, 8);
                foreach (char bad in Path.GetInvalidFileNameChars())
                    lyric = lyric.Replace(bad, '_');
                File.WriteAllBytes(
                    Path.Combine(dir, $"{stamp}_seg{k + 1}_p{i + 1}_{lyric}.wav"),
                    m_PracticePhrases[i].WavBytes);
            }
            File.WriteAllBytes(
                Path.Combine(dir, $"{stamp}_composed.wav"),
                EncodeMonoPcm16Wav(composed, rate));
            Debug.Log($"[Singing/Dump] 练唱素材已写入 {dir}（{sequence.Count} 段 + 合成源）");
        }
        catch (Exception exc)
        {
            Debug.LogWarning("[Singing/Dump] 写入练唱素材失败: " + exc.Message);
        }
    }

    private static int ComputePracticeSignature(byte[] wavBytes, float[] timeline)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (wavBytes != null ? wavBytes.Length : 0);
            hash = hash * 31 + (timeline != null ? timeline.Length : 0);
            if (wavBytes != null && wavBytes.Length > 0)
            {
                int stride = Mathf.Max(1, wavBytes.Length / 64);
                for (int i = 0; i < wavBytes.Length; i += stride)
                    hash = hash * 31 + wavBytes[i];
            }
            return hash;
        }
    }

    private static bool TryDecodePcmWav(byte[] wavBytes, out float[] mono, out int sampleRate)
    {
        mono = null;
        sampleRate = 0;
        if (wavBytes == null || wavBytes.Length < 44 ||
            wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I' ||
            wavBytes[2] != (byte)'F' || wavBytes[3] != (byte)'F' ||
            wavBytes[8] != (byte)'W' || wavBytes[9] != (byte)'A' ||
            wavBytes[10] != (byte)'V' || wavBytes[11] != (byte)'E')
            return false;

        int format = 0;
        int channels = 0;
        int bits = 0;
        int blockAlign = 0;
        int dataOffset = -1;
        int dataLength = 0;
        int cursor = 12;
        while (cursor + 8 <= wavBytes.Length)
        {
            int chunkLength = BitConverter.ToInt32(wavBytes, cursor + 4);
            int chunkData = cursor + 8;
            if (chunkLength < 0 || chunkData > wavBytes.Length ||
                chunkLength > wavBytes.Length - chunkData)
                return false;
            bool isFormat = wavBytes[cursor] == (byte)'f' &&
                wavBytes[cursor + 1] == (byte)'m' &&
                wavBytes[cursor + 2] == (byte)'t' &&
                wavBytes[cursor + 3] == (byte)' ';
            bool isData = wavBytes[cursor] == (byte)'d' &&
                wavBytes[cursor + 1] == (byte)'a' &&
                wavBytes[cursor + 2] == (byte)'t' &&
                wavBytes[cursor + 3] == (byte)'a';
            if (isFormat && chunkLength >= 16)
            {
                format = BitConverter.ToUInt16(wavBytes, chunkData);
                channels = BitConverter.ToUInt16(wavBytes, chunkData + 2);
                sampleRate = BitConverter.ToInt32(wavBytes, chunkData + 4);
                blockAlign = BitConverter.ToUInt16(wavBytes, chunkData + 12);
                bits = BitConverter.ToUInt16(wavBytes, chunkData + 14);
            }
            if (isData)
            {
                dataOffset = chunkData;
                dataLength = chunkLength;
                break;
            }
            cursor = chunkData + chunkLength + (chunkLength & 1);
        }
        if (dataOffset < 0 || sampleRate < 8000 || channels < 1 || blockAlign < 1)
            return false;
        bool pcm16 = format == 1 && bits == 16;
        bool float32 = format == 3 && bits == 32;
        if (!pcm16 && !float32) return false;

        int frameCount = dataLength / blockAlign;
        if (frameCount <= 0) return false;
        mono = new float[frameCount];
        int bytesPerSample = bits / 8;
        for (int frame = 0; frame < frameCount; frame++)
        {
            float sum = 0f;
            int frameOffset = dataOffset + frame * blockAlign;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = frameOffset + channel * bytesPerSample;
                sum += pcm16
                    ? BitConverter.ToInt16(wavBytes, offset) / 32768f
                    : BitConverter.ToSingle(wavBytes, offset);
            }
            mono[frame] = Mathf.Clamp(sum / channels, -1f, 1f);
        }
        return true;
    }

    private static byte[] EncodeMonoPcm16Wav(float[] samples, int sampleRate)
    {
        if (samples == null || samples.Length == 0 || sampleRate < 8000) return null;
        using (var stream = new MemoryStream(44 + samples.Length * 2))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + samples.Length * 2);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(samples.Length * 2);
            for (int i = 0; i < samples.Length; i++)
                writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(samples[i], -1f, 1f) * 32767f));
            writer.Flush();
            return stream.ToArray();
        }
    }

    private static float[] ResampleToRate(float[] samples, int sourceRate, int targetRate)
    {
        if (samples == null || samples.Length == 0 || sourceRate == targetRate) return samples;
        int outputLength = Mathf.Max(1, Mathf.RoundToInt(samples.Length * targetRate / (float)sourceRate));
        return ResampleLinear(samples, outputLength);
    }

    private static float[] ResampleForPace(float[] samples, float pace)
    {
        if (samples == null || samples.Length == 0) return samples;
        int outputLength = Mathf.Max(1, Mathf.RoundToInt(samples.Length / Mathf.Max(0.5f, pace)));
        return ResampleLinear(samples, outputLength);
    }

    private static float[] ResampleLinear(float[] samples, int outputLength)
    {
        if (samples == null || samples.Length == 0 || outputLength <= 0) return new float[0];
        if (samples.Length == outputLength)
        {
            float[] clone = new float[samples.Length];
            Array.Copy(samples, clone, samples.Length);
            return clone;
        }
        float[] output = new float[outputLength];
        float scale = outputLength > 1 ? (samples.Length - 1f) / (outputLength - 1f) : 0f;
        for (int i = 0; i < outputLength; i++)
        {
            float sourcePosition = i * scale;
            int left = Mathf.FloorToInt(sourcePosition);
            int right = Mathf.Min(samples.Length - 1, left + 1);
            output[i] = Mathf.Lerp(samples[left], samples[right], sourcePosition - left);
        }
        return output;
    }

    private static void ApplyPerformanceEnvelope(
        float[] samples,
        int sampleRate,
        float startGain,
        float endGain)
    {
        if (samples == null || samples.Length == 0) return;
        for (int i = 0; i < samples.Length; i++)
        {
            float t = samples.Length > 1 ? i / (float)(samples.Length - 1) : 0f;
            // One gentle arch adds a natural phrase-level swell without tremolo.
            float arch = 1f + 0.025f * Mathf.Sin(t * Mathf.PI);
            float gain = Mathf.Lerp(startGain, endGain, t) * arch;
            samples[i] = Mathf.Clamp(samples[i] * gain, -1f, 1f);
        }
    }

    private static void ApplyShortEdgeFade(float[] samples, int sampleRate, float seconds)
    {
        if (samples == null || samples.Length < 2 || sampleRate <= 0) return;
        int count = Mathf.Clamp(Mathf.RoundToInt(seconds * sampleRate), 1, samples.Length / 2);
        for (int i = 0; i < count; i++)
        {
            float gain = (i + 1f) / count;
            samples[i] *= gain;
            samples[samples.Length - 1 - i] *= gain;
        }
    }

    private static void AppendResampledTimeline(
        List<float> destination,
        float[] source,
        float sourceFrameSeconds,
        float destinationFrameSeconds,
        float pace)
    {
        if (source == null || source.Length == 0) return;
        float duration = source.Length * Mathf.Max(0.02f, sourceFrameSeconds) /
            Mathf.Max(0.5f, pace);
        int outputFrames = Mathf.Max(1, Mathf.RoundToInt(duration / destinationFrameSeconds));
        for (int i = 0; i < outputFrames; i++)
        {
            float sourceTime = i * destinationFrameSeconds * pace;
            int sourceIndex = Mathf.Clamp(
                Mathf.RoundToInt(sourceTime / Mathf.Max(0.02f, sourceFrameSeconds)),
                0,
                source.Length - 1);
            destination.Add(source[sourceIndex]);
        }
    }

    /// <summary>
    /// Return a snapshot of the latest fixed-rate playable melody.  Values at or below zero are
    /// rests.  The copy prevents a later ASR response from mutating an already queued hum-back.
    /// </summary>
    public bool TryGetRecentSingingPerformance(
        out float[] midiTimeline,
        out float frameSeconds,
        out string language)
    {
        midiTimeline = null;
        frameSeconds = m_LastSingingPerformanceFrameSeconds;
        language = m_LastSingingPerformanceLanguage ?? "";
        if (!HasPlayablePitchTimeline(m_LastSingingPerformanceMidi) ||
            Time.realtimeSinceStartup - m_LastSingingPerformanceTime > m_SingingAudioRetentionSeconds)
            return false;

        midiTimeline = new float[m_LastSingingPerformanceMidi.Length];
        Array.Copy(m_LastSingingPerformanceMidi, midiTimeline, midiTimeline.Length);
        return true;
    }

    /// <summary>
    /// Returns the symbolic score used by singing synthesis. It contains no source audio.
    /// </summary>
    public bool TryGetRecentSingingScoreJson(
        out string scoreJson,
        out string lyrics,
        out string language)
    {
        scoreJson = "";
        lyrics = m_LastSingingLyrics ?? "";
        language = m_LastSingingPerformanceLanguage ?? "";
        if (m_LastSingingPerformanceScore == null ||
            m_LastSingingPerformanceScore.schema_version <= 0 ||
            Time.realtimeSinceStartup - m_LastSingingPerformanceTime >
                m_SingingAudioRetentionSeconds)
            return false;

        scoreJson = JsonUtility.ToJson(m_LastSingingPerformanceScore);
        return !string.IsNullOrEmpty(scoreJson);
    }

    public void RememberSong(
        string songId,
        string title,
        string artist,
        string lyrics,
        string aliases,
        string reason,
        Action<SongMemoryResult> callback)
    {
        if (!HasFreshSingingAudio())
        {
            if (callback != null) callback(new SongMemoryResult
            {
                Ok = false,
                Action = "remember",
                Error = "最近没有可保存的歌唱或哼唱音频；可以请用户再唱一小段。",
            });
            return;
        }
        WWWForm form = new WWWForm();
        form.AddField("song_id", songId ?? "");
        form.AddField("title", title ?? "");
        form.AddField("artist", artist ?? "");
        form.AddField("lyrics", lyrics ?? "");
        form.AddField("aliases", aliases ?? "");
        form.AddField("reason", reason ?? "");
        form.AddBinaryData("audio_file", m_LastSingingAudioBytes, "remembered_singing.wav", "audio/wav");
        StartCoroutine(SendSongMemoryRequest(m_SongRememberURL, form, "remember", callback));
    }

    public void RenameRememberedSong(
        string songId,
        string title,
        string artist,
        string aliases,
        Action<SongMemoryResult> callback)
    {
        WWWForm form = new WWWForm();
        form.AddField("song_id", songId ?? "");
        form.AddField("title", title ?? "");
        form.AddField("artist", artist ?? "");
        form.AddField("aliases", aliases ?? "");
        StartCoroutine(SendSongMemoryRequest(m_SongRenameURL, form, "rename", callback));
    }

    public void ForgetRememberedSong(string songId, Action<SongMemoryResult> callback)
    {
        WWWForm form = new WWWForm();
        form.AddField("song_id", songId ?? "");
        StartCoroutine(SendSongMemoryRequest(m_SongForgetURL, form, "forget", callback));
    }

    /// <summary>
    /// Resolve a persistent song memory into real source singing.  continue/auto also sends
    /// the newest local singing clip so the service can align its lyrics/melody position.
    /// </summary>
    public void SingRememberedSong(
        string songId,
        string title,
        string mode,
        float maxSeconds,
        int seed,
        string reason,
        Action<SongPerformanceResult> callback,
        string segmentLyrics = "")
    {
        StartCoroutine(SendRememberedSongPerformance(
            songId, title, mode, maxSeconds, seed, reason, segmentLyrics, callback));
    }

    private IEnumerator SendRememberedSongPerformance(
        string songId,
        string title,
        string mode,
        float maxSeconds,
        int seed,
        string reason,
        string segmentLyrics,
        Action<SongPerformanceResult> callback)
    {
        string normalizedMode = string.IsNullOrWhiteSpace(mode)
            ? "memory"
            : mode.Trim().ToLowerInvariant();
        bool needsAlignment = normalizedMode == "continue" || normalizedMode == "auto";
        WWWForm form = new WWWForm();
        form.AddField("song_id", songId ?? "");
        form.AddField("title", title ?? "");
        form.AddField("mode", normalizedMode);
        form.AddField("query", needsAlignment ? (m_LastSingingLyrics ?? "") : "");
        //按歌词点某一段：给了就只唱那一段，服务端会忽略 mode。用户是用词句指段的
        //（"那段 can you give me one last kiss 怎么唱"），不是用序号——而她也看不到序号。
        form.AddField("segment_lyrics", (segmentLyrics ?? "").Trim());
        form.AddField("max_seconds", Mathf.Clamp(maxSeconds, 3f, 180f).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        form.AddField("seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        bool hasFreshAudio = needsAlignment && HasFreshSingingAudio();
        if (hasFreshAudio)
            form.AddBinaryData("audio_file", m_LastSingingAudioBytes, "continuation_query.wav", "audio/wav");

        if (m_VerboseLog)
        {
            Debug.Log($"[SongSing] mode={normalizedMode} id={songId} title=\"{title}\" " +
                $"alignmentAudio={hasFreshAudio} seed={seed} reason=\"{reason}\"");
        }

        using (UnityWebRequest www = UnityWebRequest.Post(m_SongSingURL, form))
        {
            www.SetRequestHeader("accept", "application/json");
            yield return www.SendWebRequest();
            SongPerformanceResponse response = null;
            try { response = JsonUtility.FromJson<SongPerformanceResponse>(www.downloadHandler.text); }
            catch (Exception e) { Debug.LogWarning("[SongSing] JSON 解析失败: " + e.Message); }

            if (www.result != UnityWebRequest.Result.Success || response == null || !response.ok)
            {
                string detail = response != null && !string.IsNullOrWhiteSpace(response.error)
                    ? response.error
                    : www.error + " / " + www.downloadHandler.text;
                if (callback != null) callback(new SongPerformanceResult
                {
                    Ok = false,
                    Mode = normalizedMode,
                    Error = detail,
                });
                yield break;
            }

            byte[] wavBytes = null;
            try { wavBytes = Convert.FromBase64String(response.audio_base64 ?? ""); }
            catch (Exception e)
            {
                if (callback != null) callback(new SongPerformanceResult
                {
                    Ok = false,
                    Mode = normalizedMode,
                    Error = "本地曲库音频解码失败: " + e.Message,
                });
                yield break;
            }
            if (wavBytes == null || wavBytes.Length <= 44 ||
                response.pitch_timeline_midi == null || response.pitch_timeline_midi.Length == 0)
            {
                if (callback != null) callback(new SongPerformanceResult
                {
                    Ok = false,
                    Mode = normalizedMode,
                    Error = "本地曲库没有返回可播放的歌声或旋律时间轴。",
                });
                yield break;
            }

            if (callback != null) callback(new SongPerformanceResult
            {
                Ok = true,
                SongId = response.song_id ?? "",
                Title = response.title ?? "",
                Artist = response.artist ?? "",
                DisplayName = response.display_name ?? "",
                Mode = response.mode ?? normalizedMode,
                WavBytes = wavBytes,
                MidiTimeline = response.pitch_timeline_midi,
                FrameSeconds = response.pitch_timeline_frame_seconds > 0f
                    ? response.pitch_timeline_frame_seconds : 0.10f,
                DurationSeconds = response.duration_seconds,
                ReferenceCount = response.reference_count,
                UniqueSegmentCount = response.unique_segment_count,
                DuplicateVariantCount = response.duplicate_variant_count,
                SelectedSegmentCount = response.selected_segment_count,
                MatchConfidence = response.match_confidence,
                LyricsConfidence = response.lyrics_confidence,
                Continuation = response.continuation,
                ContinuationBasis = response.continuation_basis ?? "",
                Error = response.error ?? "",
            });
        }
    }

    private IEnumerator SendSongMemoryRequest(
        string url,
        WWWForm form,
        string action,
        Action<SongMemoryResult> callback)
    {
        using (UnityWebRequest www = UnityWebRequest.Post(url, form))
        {
            www.SetRequestHeader("accept", "application/json");
            yield return www.SendWebRequest();
            if (www.result != UnityWebRequest.Result.Success)
            {
                SongMemoryResult failed = new SongMemoryResult
                {
                    Ok = false,
                    Action = action,
                    Error = www.error + " / " + www.downloadHandler.text,
                };
                Debug.LogWarning("[SongMemory] 请求失败: " + failed.Error);
                if (callback != null) callback(failed);
                yield break;
            }

            SongMemoryResponse response = null;
            try { response = JsonUtility.FromJson<SongMemoryResponse>(www.downloadHandler.text); }
            catch (Exception e) { Debug.LogWarning("[SongMemory] JSON 解析失败: " + e.Message); }
            SongMemoryResult result = new SongMemoryResult();
            if (response == null)
            {
                result.Ok = false;
                result.Action = action;
                result.Error = "invalid response";
            }
            else
            {
                result.Ok = response.ok;
                result.Action = response.action ?? action;
                result.SongId = response.song_id ?? "";
                result.ClipId = response.clip_id ?? "";
                result.Title = response.title ?? "";
                result.Artist = response.artist ?? "";
                result.DisplayName = response.display_name ?? "";
                result.WavFile = response.wav_file ?? "";
                result.Named = response.named;
                result.ReferenceCount = response.reference_count;
                result.UniqueSegmentCount = response.unique_segment_count;
                result.DuplicateVariantCount = response.duplicate_variant_count;
                result.SegmentStatus = response.segment_status ?? "";
                result.MergeNote = response.merge_note ?? "";
                result.Error = response.error ?? "";
            }
            if (callback != null) callback(result);
        }
    }

    private string ExtractIntroducedName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var patterns = new string[]
        {
            @"(?:我叫|请叫我|叫我)(?<name>[\p{L}\p{N}_·・]{1,12})(?:[，。！？,.!?]|$)",
            @"(?:私|僕|俺)は(?<name>[\p{L}\p{N}_·・]{1,12}?)(?:です|だ|と申します)(?:[。！!]|$)",
            @"(?:my name is|call me)\s+(?<name>[A-Za-z][A-Za-z0-9 _'\-]{0,30})(?:[,.!?]|$)"
        };
        for (int i = 0; i < patterns.Length; i++)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                patterns[i],
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string name = StripTrailingParticles(match.Groups["name"].Value.Trim());
            if (name.Length > 0 && name.Length <= 32) return name;
        }
        return "";
    }

    /// <summary>
    /// 去掉名字尾部的语气词。正则不知道「吧」不是名字的一部分：
    /// 用户说「就叫我小优吧。」，`叫我(...)[，。！？]` 会一路吃到句号，抓出「小优吧」，
    /// 然后被自动写进声纹档案，元数据里就一直显示 [说话人:小优吧]。
    /// 只剥尾部、且不把名字剥空——「吧」本身可以是名字的一部分(极少见但不该误伤)。
    /// 这条只是兜底；她自己用 &lt;speaker_name/&gt; 给的名字优先级更高、质量也更好。
    /// </summary>
    private static string StripTrailingParticles(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        //中文句末语气词 + 日语的 だ/です 残留
        string particles = "吧呀啊呐呢哦喔啦嘛咯了的";
        int end = name.Length;
        while (end > 1 && particles.IndexOf(name[end - 1]) >= 0) end--;
        return end == name.Length ? name : name.Substring(0, end);
    }

    private bool IsInArray(string s, string[] arr)
    {
        if (arr == null) return false;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] == s) return true;
        }
        return false;
    }

    #region 数据定义

    [Serializable]
    public class SingingNote
    {
        public int midi = 0;
        public string note_name = "";
        public float start_seconds = 0f;
        public float duration_seconds = 0f;
        public string note_type = "";
        public float confidence = 0f;
    }

    [Serializable]
    public class SingingVibrato
    {
        public float start_seconds = 0f;
        public float duration_seconds = 0f;
        public float rate_hz = 0f;
        public float depth_semitones = 0f;
        public float confidence = 0f;
    }

    [Serializable]
    public class SingingScore
    {
        public int schema_version = 0;
        public string source = "";
        public string extractor_backend = "";
        public string language = "";
        public string lyrics = "";
        public string lyrics_reading = "";
        public string lyrics_reading_source = "";
        public bool lyrics_reading_complete = false;
        public string[] lyrics_mora = null;
        public string lyrics_alignment = "";
        public bool lyrics_override = false;
        public float duration_seconds = 0f;
        public float frame_seconds = 0.01f;
        public float confidence = 0f;
        public SingingNote[] notes = null;
        public float[] f0_hz = null;
        public float[] energy = null;
        public float[] breath_positions_seconds = null;
        public SingingVibrato[] vibrato = null;
    }

    [Serializable]
    private class Response
    {
        public string text = "";
        public string language = "";
        public string emotion = "";
        public string audio_event = "";
        public string speaker_id = "";
        public string speaker_name = "";
        public string speaker_kind = "";
        public string speaker_status = "";
        public float speaker_confidence = 0f;
        public float speaker_enrollment_progress = 0f;
        public bool speaker_is_new = false;
        public bool speaker_persistent = false;
        public bool no_speech = false;
        public float speaker_elapsed = 0f;
        public int speech_ms = 0;
        public float vad_elapsed = 0f;
        public float elapsed = 0f;
        public bool singing_analysis_available = false;
        public bool is_singing = false;
        public float singing_probability = 0f;
        public string pitch_backend = "";
        public float voiced_ratio = 0f;
        public float pitch_stability = 0f;
        public float sustained_ratio = 0f;
        public float pitch_min_hz = 0f;
        public float pitch_max_hz = 0f;
        public float pitch_median_hz = 0f;
        public string pitch_low_note = "";
        public string pitch_high_note = "";
        public string pitch_median_note = "";
        public string note_sequence = "";
        public string singing_summary = "";
        public float[] pitch_contour_midi = null;
        public float[] pitch_timeline_midi = null;
        public float pitch_timeline_frame_seconds = 0.10f;
        public float singing_start_seconds = 0f;
        //歌声岛的结束位置（内容坐标系，与 singing_start_seconds 同一原点）。
        //服务端找不到可信边界时会回填整段时长，等价于"不裁尾"。
        public float singing_end_seconds = 0f;
        //只对裁出来那段单独再识别一次得到的歌词。没发生裁剪时为空。
        public string singing_text = "";
        //唱完之后那截说话的单独转写。整轮 ASR 在长混合录音上只转得出开头，
        //「这一段不算，我们重新唱」这种话此前没有任何子系统看得见。
        public string singing_tail_text = "";
        //随分段歌词一起来的语言与假名。整轮可能是 zh 而唱的那段是 ja，
        //沿用整轮标签会让 9883 用错 G2P、整份乐谱被弃用。
        public string singing_language = "";
        public string singing_lyrics_reading = "";
        public string singing_lyrics_reading_source = "";
        public bool singing_lyrics_reading_complete = false;
        public string[] singing_lyrics_mora = null;
        public float pitch_timeline_start_seconds = 0f;
        public float audio_content_start_seconds = 0f;
        public SingingScore singing_score = null;
        //曲库里旋律接近的几条，服务端每个唱歌轮自动算好。不是识别结果——
        //旋律相似度本身分不开不同的歌(实测组内中位 0.701 / 跨组 0.675)，
        //所以这里只给候选和证据，谁是谁由她看歌词自己判断。
        public SongRecall[] song_recall = null;
    }

    [Serializable]
    public class SongRecall
    {
        public string song_id = "";
        public string display_name = "";
        public bool named = false;
        public string lyrics = "";
        //旋律的文本形式。8/17 实测四选一：只给歌词 50%、只给旋律 58%、两个都给 66%
        //——互补而非冗余。曲库歌词来自唱歌 ASR、错字多，音高提取相对稳。
        public string note_sequence = "";
        public float confidence = 0f;
        public float melody_score = 0f;
        //较短那条里有多少音在另一条里找到了。用户唱一小段问"记不记得"时，
        //这个数比对称相似度直观：唱 6 个音全命中就是 1.00，而对称分只有 0.21。
        public float containment = 0f;
        public string match_reason = "";
        public long last_heard = 0;
        public int take_count = 0;
    }

    [Serializable]
    private class VadResponse
    {
        public bool is_speech = false;
        public int speech_ms = 0;
        public string speaker_id = "";
        public string speaker_name = "";
        public string speaker_kind = "";
        public string speaker_status = "";
        public float speaker_confidence = 0f;
        public float speaker_self_confidence = 0f;
        public float elapsed = 0f;
        public bool is_singing = false;
        public float singing_probability = 0f;
    }

    public class VoiceActivityResult
    {
        public bool IsSpeech = false;
        public int SpeechMs = 0;
        public string SpeakerId = "";
        public string SpeakerName = "";
        public string SpeakerKind = "";
        public string SpeakerStatus = "";
        public float SpeakerConfidence = 0f;
        public float SelfConfidence = 0f;
        public bool IsSinging = false;
        public float SingingProbability = 0f;
    }

    [Serializable]
    private class SongSearchResponse
    {
        public bool ok = false;
        public bool reliable = false;
        public string query = "";
        public string mode = "";
        public string summary = "";
        public string privacy = "";
        public string error = "";
        public SongMatch[] matches = null;
    }

    [Serializable]
    private class SongMemoryResponse
    {
        public bool ok = false;
        public string action = "";
        public string song_id = "";
        public string clip_id = "";
        public string title = "";
        public string artist = "";
        public string display_name = "";
        public string wav_file = "";
        public bool named = false;
        public int reference_count = 0;
        public int unique_segment_count = 0;
        public int duplicate_variant_count = 0;
        public string segment_status = "";
        public string merge_note = "";
        public string error = "";
    }

    [Serializable]
    private class SongPerformanceResponse
    {
        public bool ok = false;
        public string action = "";
        public string song_id = "";
        public string title = "";
        public string artist = "";
        public string display_name = "";
        public string mode = "";
        public string audio_base64 = "";
        public float[] pitch_timeline_midi = null;
        public float pitch_timeline_frame_seconds = 0.10f;
        public float duration_seconds = 0f;
        public int reference_count = 0;
        public int unique_segment_count = 0;
        public int duplicate_variant_count = 0;
        public int selected_segment_count = 0;
        public float match_confidence = 0f;
        public float lyrics_confidence = 0f;
        public bool continuation = false;
        public string continuation_basis = "";
        public string error = "";
    }

    [Serializable]
    public class SongMatch
    {
        public string title = "";
        public string artist = "";
        public string album = "";
        public string source = "";
        public string source_id = "";
        public float confidence = 0f;
        public string match_reason = "";
        public string url = "";
    }

    public class SongSearchResult
    {
        public bool Ok = false;
        public bool Reliable = false;
        public string Query = "";
        public string Mode = "";
        public string Summary = "";
        public string Privacy = "";
        public string Error = "";
        public SongMatch[] Matches = new SongMatch[0];
    }

    public class SongMemoryResult
    {
        public bool Ok = false;
        public string Action = "";
        public string SongId = "";
        public string ClipId = "";
        public string Title = "";
        public string Artist = "";
        public string DisplayName = "";
        public string WavFile = "";
        public bool Named = false;
        public int ReferenceCount = 0;
        public int UniqueSegmentCount = 0;
        public int DuplicateVariantCount = 0;
        public string SegmentStatus = "";
        //追加到已有条目时，服务端对这次合并的依据说了什么。
        public string MergeNote = "";
        public string Error = "";
    }

    public class SongPerformanceResult
    {
        public bool Ok = false;
        public string SongId = "";
        public string Title = "";
        public string Artist = "";
        public string DisplayName = "";
        public string Mode = "";
        public byte[] WavBytes = null;
        public float[] MidiTimeline = null;
        public float FrameSeconds = 0.10f;
        public float DurationSeconds = 0f;
        public int ReferenceCount = 0;
        public int UniqueSegmentCount = 0;
        public int DuplicateVariantCount = 0;
        public int SelectedSegmentCount = 0;
        public float MatchConfidence = 0f;
        public float LyricsConfidence = 0f;
        public bool Continuation = false;
        public string ContinuationBasis = "";
        public string Error = "";
    }

    #endregion
}
