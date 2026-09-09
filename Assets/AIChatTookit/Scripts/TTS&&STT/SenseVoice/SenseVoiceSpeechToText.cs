using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
public partial class SenseVoiceSpeechToText : STT
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
    /// <summary>最近一次匹配到的具体声纹；LastSpeakerId 是稳定的身份文件夹 ID。</summary>
    public string LastSpeakerVoiceprintId { get; private set; } = "";
    public string LastSpeakerVoiceprintStatus { get; private set; } = "";
    /// <summary>最近一次**真的认出**的非 AI 说话人；噪音/无人声轮次不会擦掉它。</summary>
    public string LastKnownSpeakerId { get; private set; } = "";
    public string LastKnownSpeakerName { get; private set; } = "";
    public string LastSpeakerStatus { get; private set; } = "";
    public float LastSpeakerConfidence { get; private set; } = 0f;
    /// <summary>服务端认为本轮音频属于角色自身外放声纹的置信度。</summary>
    public float LastSpeakerSelfConfidence { get; private set; } = 0f;
    public float LastSpeakerEnrollmentProgress { get; private set; } = 0f;
    public bool LastSpeakerIsNew { get; private set; } = false;
    public bool LastSpeakerPersistent { get; private set; } = false;
    public bool LastIsSinging { get; private set; } = false;
    public float LastSingingProbability { get; private set; } = 0f;
    public float LastPitchStability { get; private set; } = 0f;
    public bool LastSingingAnalysisAvailable { get; private set; } = false;
    public float LastSingingIslandSeconds { get; private set; } = 0f;
    public float LastSingingContentSeconds { get; private set; } = 0f;
    public float LastSingingIslandRatio { get; private set; } = 0f;
    /// <summary>
    /// 声学这里只报告证据质量：0.52≤p&lt;0.58 是 uncertain，不等于说话，
    /// 也不等于已经确认歌唱。
    /// </summary>
    public bool LastAcousticModeUncertain
    {
        get
        {
            return LastSingingAnalysisAvailable && !LastNoSpeech &&
                IsAcousticProbabilityUncertain(LastSingingProbability);
        }
    }
    public string LastAcousticMode
    {
        get
        {
            if (!LastSingingAnalysisAvailable || LastNoSpeech) return "unavailable";
            if (LastAcousticModeUncertain) return "uncertain";
            return LastIsSinging ? "singing" : "speech";
        }
    }
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
        m_SongCatalogURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog";
        m_SongRememberURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/remember";
        m_SongRenameURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/rename";
        m_SongForgetURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/forget";
        m_SongSingURL = m_ServerSetting.TrimEnd('/') + "/songs/catalog/sing";
    }

    private string m_VadRecognizeURL;
    private string m_SongSearchURL;
    private string m_SongCatalogURL;
    private string m_SongRememberURL;
    private string m_SongRenameURL;
    private string m_SongForgetURL;
    private string m_SongSingURL;
    private byte[] m_LastSingingAudioBytes;
    //默认缓存保持“干净边界”；这一份是同一轮的可恢复扩展边界。它只在角色明确
    //选择 capture="expanded" 时使用，避免为了防漏唱而默认把唱前/唱后的说话变声。
    private byte[] m_LastSingingRecoveryAudioBytes;
    private float[] m_LastSingingRecoveryPerformanceMidi = new float[0];
    //置信度分带的正式证据边界。0.52~0.58 不再被二值化成 speech，而是报告 uncertain，
    //触发完整转写语义复核；它本身不触发自动回唱或机械追问。
    //0.52 来自 8/30 实测漏判：真唱 p=0.54、stab=0.69、岛占 93%。更低的历史样本
    //仍有较多普通说话，先不扩大复核范围。
    // 岛比流式起唱点晚多少之内仍然信岛。见下方 onsetsAgree 处的推导。
    private const float k_IslandLaterToleranceSeconds = 1.75f;
    private const float k_SingingBandLow = 0.52f;
    private const float k_SingingBandHigh = 0.58f;

    public static bool IsAcousticProbabilityUncertain(float probability)
    {
        return probability >= k_SingingBandLow &&
            probability < k_SingingBandHigh;
    }

    private static string DescribeSingingBand(float probability)
    {
        if (probability < k_SingingBandLow) return "低区";
        if (probability >= k_SingingBandHigh) return "高区";
        return "模糊带";
    }

    private string m_LastSingingLyrics = "";
    //服务端对裁出来那段单独识别得到的歌词。整轮转写含唱前唱后的说话，
    //拿它当歌词会被 SVS 硬塞进几秒的旋律里，唱出来听不清。
    private string m_LastResponseSingingText = "";
    private TurnTranscriptSegment[] m_LastTurnSegments;
    private readonly List<string> m_EarlierCaptureTranscripts = new List<string>();
    private string m_CurrentCaptureTranscript = "";
    private float m_CurrentCaptureTranscriptSeconds;
    private string m_LastWholeTurnText = "";
    private string m_LastSegmentedPrimaryText = "";
    public bool HasTimeOrderedTranscript => m_LastTurnSegments != null &&
        m_LastTurnSegments.Length > 0 && LastText == m_LastSegmentedPrimaryText;
    //本轮响应给出的头部裁剪量。每份响应都会重写，所以不会串轮。
    private float m_LastResponseAudioCropSeconds = 0f;
    //岛结束之后被丢掉的那段音频有多长。快速回唱播的是**整条录音**，
    //所以这一段会被原样唱回去——8/25 实测岛只有 0~10.17s、录音 20.95s，
    //后面 10.8 秒的「歌词唱错了，歌词唱错了」被转成她的声线放了回来。
    private float m_LastResponseAudioTailDropSeconds = 0f;
    private string m_LastResponseSingingTailText = "";
    //clean 之外、expanded 之内的边界证据。服务端只分析这两小段，保存时间戳、
    //短 ASR 与声学类型；它们只提供给 LLM 判断，程序不会据此擅自选 clean/expanded。
    private float m_LastHeadExtraSeconds = 0f;
    private float m_LastTailExtraSeconds = 0f;
    private string m_LastHeadExtraText = "";
    private string m_LastTailExtraText = "";
    private string m_LastHeadExtraType = "none";
    private string m_LastTailExtraType = "none";
    private float m_LastHeadExtraProbability = 0f;
    private float m_LastTailExtraProbability = 0f;
    private bool m_LastHeadExtraReviewRequired = false;
    private bool m_LastTailExtraReviewRequired = false;
    private float m_LastHeadExtraMelodicSeconds = 0f;
    private float m_LastTailExtraMelodicSeconds = 0f;
    private float m_LastHeadExtraMelodicRatio = 0f;
    private float m_LastTailExtraMelodicRatio = 0f;
    private float m_LastHeadExtraLongestMelodicRunSeconds = 0f;
    private float m_LastTailExtraLongestMelodicRunSeconds = 0f;
    private SingingBoundarySubsegment[] m_LastHeadExtraSegments =
        new SingingBoundarySubsegment[0];
    private SingingBoundarySubsegment[] m_LastTailExtraSegments =
        new SingingBoundarySubsegment[0];
    //流式起唱保护可能把 acoustic clean 起点之前的一小段保留进 clean。
    //它不再属于 head_extra，却仍可能包含口语，必须作为独立事实交给 LLM。
    private float m_LastCleanLeadInUnverifiedSeconds = 0f;
    private string m_LastCleanLeadInEvidenceText = "";
    private string m_LastCleanLeadInEvidenceType = "none";
    private float m_LastCleanLeadInEvidenceProbability = 0f;
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
    private byte[] m_RollbackSingingRecoveryAudioBytes;
    private string m_RollbackSingingLyrics = "";
    private float m_RollbackSingingAudioTime = -999f;
    private float m_RollbackSingingPerformanceTime = -999f;
    private float[] m_RollbackSingingPerformanceMidi = new float[0];
    private float[] m_RollbackSingingRecoveryPerformanceMidi = new float[0];
    private float m_RollbackSingingPerformanceFrameSeconds = 0.10f;
    private string m_RollbackSingingPerformanceLanguage = "";
    private SingingScore m_RollbackSingingPerformanceScore = null;
    private SingingEvidenceSnapshot m_RollbackSingingCacheEvidence;
    private int m_RollbackSingingCacheCaptureSessionSerial = 0;
    private SingingEvidenceSnapshot m_LastSingingCacheEvidence;
    private int m_LastSingingCacheCaptureSessionSerial = 0;
    // A practice session is intentionally separate from the persistent song catalogue.
    // It keeps only final-ASR-confirmed performances, in the order the user sang them,
    // so ChatSample can later render the practiced phrases as one continuous take.
    [Serializable]
    public sealed class SingingBoundarySubsegment
    {
        public float start_seconds;
        public float end_seconds;
        public float expanded_start_seconds;
        public float expanded_end_seconds;
        public string type;
        public float voiced_ratio;
        public float pitch_smooth_ratio;
    }

    private sealed class PracticePhrase
    {
        // One identity from quarantine through confirmation/revision. Never derived
        // from list position, candidate number, or the latest-audio cache.
        public string ClipRef = "clip:" + Guid.NewGuid().ToString("N");
        public SingingEvidenceSnapshot RecordingEvidence;
        public SingingEvidenceSnapshot LatestRecordingEvidence;
        public int RecordingSequence;
        public PracticePhrase CopyForRevision() => (PracticePhrase)MemberwiseClone();
        //会话内稳定身份。清单段号会在删除旧段后前移，不能拿段号给跨轮音高状态做键。
        public int StableId;
        public byte[] WavBytes;
        public byte[] RecoveryWavBytes;
        public float[] MidiTimeline;
        public float[] RecoveryMidiTimeline;
        //不可变的录音源。WavBytes/MidiTimeline 是当前可播放版本，修边后会更新；
        //下面四项始终保留首次提交的 clean/expanded，因此“重新处理”永远可撤销，
        //也不会像 practice_drop 那样把唯一的原录音永久删掉。
        public byte[] SourceCleanWavBytes;
        public byte[] SourceExpandedWavBytes;
        public float[] SourceCleanMidiTimeline;
        public float[] SourceExpandedMidiTimeline;
        public int Revision;
        public string ActiveCapture = "clean";
        public float ActiveTrimHeadSeconds;
        public float ActiveTrimTailSeconds;
        public float FrameSeconds;
        public string Language;
        public int Signature;
        //同一次真实麦克风录音的稳定身份。preview/final、clean/expanded/raw
        //都属于同一编号，不能因裁剪后的字节不同被当成另一段录音。
        public int CaptureSessionSerial;
        //身份：用户是按内容指段的(「先唱沉默着走了那段」)，不是按序号。
        //没有这些字段时感知帧只能报歌词片段，她分不清哪几段属于同一首、哪段是最近唱的
        //——8/16 实测练唱会话累到 7 段、跨两首歌，她连着三次选错段(order=1,2 / 3,5,6,1 / 7)。
        public string Lyrics;
        public float Seconds;
        public float RecoverySeconds;
        //原始源各自使用独立的 0 秒坐标。当前 Seconds 可能已经是修订后的版本，
        //不能再拿它解释最初的 head_extra / tail_extra。
        public float OriginalCleanSeconds;
        public float OriginalExpandedSeconds;
        public string SongId;      //本轮曲库回忆的首选，作为"这段属于哪首歌"的线索
        public string SongName;
        public float AtRealtime;   //唱下这一段的时刻，供"最近唱的是哪段"判断
        public float ConfirmedAtRealtime;
        //唱这一段之前用户说的最后一句话。换歌/重唱的意图就在这句里。
        public string PrecedingSpeech;
        //软降级(声学判唱、文字判说话)写进来的段落带着这一位。
        //它**不拦任何用途**——段落照样能被 order 点到、照样能唱。它只是个警示：
        //这一段有可能根本不是歌声；用户确认不是歌声，或明确要求无视/丢弃这次录音时，
        //都由角色结合完整语境调用 practice_drop，程序不靠关键词擅自删除。
        //原来软降级是整个不写，代价是文字判错时那段永远连不起来(8/24 实测四轮说不清)。
        public bool PendingConfirmation;
        //若这一段曾处于来源隔离区，保留其原始 candidate 身份。candidate id 是按
        //真实录音创建且不会因 practice 删除/重排而改变的；角色因此能把“候选 2”
        //与确认后得到的“练唱清单第 4 段”对应起来，而不是把两套编号混为一谈。
        public int OriginCandidateId;
        public float HeadExtraSeconds;
        public float TailExtraSeconds;
        public string HeadExtraText;
        public string TailExtraText;
        public string HeadExtraType;
        public string TailExtraType;
        public float HeadExtraProbability;
        public float TailExtraProbability;
        public bool HeadExtraReviewRequired;
        public bool TailExtraReviewRequired;
        public float HeadExtraMelodicSeconds;
        public float TailExtraMelodicSeconds;
        public float HeadExtraMelodicRatio;
        public float TailExtraMelodicRatio;
        public float HeadExtraLongestMelodicRunSeconds;
        public float TailExtraLongestMelodicRunSeconds;
        public SingingBoundarySubsegment[] HeadExtraSegments;
        public SingingBoundarySubsegment[] TailExtraSegments;
        public float CleanLeadInUnverifiedSeconds;
        public string CleanLeadInEvidenceText;
        public string CleanLeadInEvidenceType;
        public float CleanLeadInEvidenceProbability;
    }

    /// <summary>练唱会话里每一段的身份，供感知帧展示与顺序指定。</summary>
    public sealed class PracticePhraseInfo
    {
        public string ClipRef;
        public int RecordingSequence;
        public string RecordingTranscript;
        public float RawSeconds, CleanStartSeconds, CleanEndSeconds, ExpandedStartSeconds, ExpandedEndSeconds;
        public int Index;          //1 起，就是 order 里要写的数字
        public int StableId;       //会话内稳定，不随清单删除/前移改变
        public int Revision;       //0=原始可播放版本；>0=已非破坏性修边
        public string ActiveCapture;
        public float ActiveTrimHeadSeconds;
        public float ActiveTrimTailSeconds;
        public string Lyrics;
        public float Seconds;
        public float RecoverySeconds;
        //原始 clean / expanded 各自的完整长度。Seconds 是当前可播放版本，
        //修订后不能再拿它解释最初的边界证据。
        public float OriginalCleanSeconds;
        public float OriginalExpandedSeconds;
        public string Language;
        public string SongId;
        public string SongName;
        public float AgoSeconds;   //距现在多久唱的
        public float ConfirmedAgoSeconds;
        public int CaptureOrder;   //录音先后，绝不是 order 参数的清单编号
        public bool HasExpandedCapture;
        //同一句被教了好几遍时的分组：TakeGroup 相同 = 同一句，TakeIndex 是第几遍。
        //没有这两个字段时清单里两段歌词一模一样，用户说"第二次教你的那段"她对不上段号
        //——8/17 实测她因此把「紧闭双眼」连着唱了两遍。
        public int TakeGroup;
        public int TakeIndex;
        public int TakeTotal;
        //唱这一段之前用户说的最后一句话——「换一首歌」「刚才唱错了」都在这里。
        public string PrecedingSpeech;
        //这一段所有有声帧的中心音高（中位数，MIDI）。它是稳健的调音参照，
        //不是旋律第一个音，也不是歌曲的调性/key。
        public float PitchCenterMidi;
        public string PitchCenterNote;
        //开头第一个连续稳定的有声音高，以及去掉两端异常值后的主要音域。
        public float FirstStablePitchMidi;
        public string FirstStablePitchNote;
        public float PitchRangeLowMidi;
        public float PitchRangeHighMidi;
        public string PitchRangeNote;
        //旧字段保留给场景/扩展兼容；语义等同 PitchCenter*，不再称作“起调”。
        //用户说的「让第三段和前两段调一致」需要这个数才能落地——8/20 实测
        //段1/段2 中位 60，段3 中位 57，实际只差 3 个半音；而她当时猜的是升八度(+12)，
        //既超出 key 的取值范围被截回默认档，也远大于真实差值，于是三轮都听不出变化。
        public float PitchMedianMidi;
        //兼容旧名；内容是中心音高，不是绝对起调。
        public string PitchBaseNote;
        //见 PracticePhrase.PendingConfirmation：清单里要显出来，否则她无从知道
        //哪一段是存疑的，也就不会在用户否认时去 drop 它。
        public bool PendingConfirmation;
        public int OriginCandidateId;
        public float HeadExtraSeconds;
        public float TailExtraSeconds;
        public string HeadExtraText;
        public string TailExtraText;
        public string HeadExtraType;
        public string TailExtraType;
        public float HeadExtraProbability;
        public float TailExtraProbability;
        public bool HeadExtraReviewRequired;
        public bool TailExtraReviewRequired;
        public float HeadExtraMelodicSeconds;
        public float TailExtraMelodicSeconds;
        public float HeadExtraMelodicRatio;
        public float TailExtraMelodicRatio;
        public float HeadExtraLongestMelodicRunSeconds;
        public float TailExtraLongestMelodicRunSeconds;
        public SingingBoundarySubsegment[] HeadExtraSegments;
        public SingingBoundarySubsegment[] TailExtraSegments;
        public float CleanLeadInUnverifiedSeconds;
        public string CleanLeadInEvidenceText;
        public string CleanLeadInEvidenceType;
        public float CleanLeadInEvidenceProbability;
    }

    /// <summary>来源与播放资格独立的录音候选；只有明确选中才进入执行清单。</summary>
    public sealed class QuarantinedSingingCandidateInfo
    {
        public string ClipRef;
        public int RecordingSequence;
        public string CurrentCapture;
        public float CurrentStartSeconds, CurrentEndSeconds;
        public float RawSeconds, CleanStartSeconds, CleanEndSeconds, ExpandedStartSeconds, ExpandedEndSeconds;
        public float CleanSeconds;
        public float ExpandedSeconds;
        public bool HasRecoveryEvidence;
        public int CandidateId;    //会话内稳定，不因确认顺序或 practice 重排而改变
        public string Lyrics;
        public string PrecedingSpeech;
        public string WholeTurnText;
        public string SingingSegmentText;
        public float Seconds;
        public float AgoSeconds;
        public int CaptureOrder;   //按真实录音时间排序，1=本会话最早
        public string SourceStatus;   //pending / confirmed_user
        public string PlaybackStatus; //ready / evidence_only / unavailable
        public float SingingProbability;
        public float PitchStability;
        public float MelodicIslandSeconds;
        public float ContentSeconds;
        public float HeadExtraSeconds;
        public float TailExtraSeconds;
        public string HeadExtraText;
        public string TailExtraText;
        public string HeadExtraType;
        public string TailExtraType;
        public bool HeadExtraReviewRequired;
        public bool TailExtraReviewRequired;
        public float HeadExtraMelodicSeconds;
        public float TailExtraMelodicSeconds;
        public float HeadExtraMelodicRatio;
        public float TailExtraMelodicRatio;
        public float HeadExtraLongestMelodicRunSeconds;
        public float TailExtraLongestMelodicRunSeconds;
        public SingingBoundarySubsegment[] HeadExtraSegments;
        public SingingBoundarySubsegment[] TailExtraSegments;
        public float CleanLeadInUnverifiedSeconds;
        public string CleanLeadInEvidenceText;
        public string CleanLeadInEvidenceType;
        public float CleanLeadInEvidenceProbability;
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
        //各段自己的中心音高(MIDI)。移调后的结果 = 这个数 + 实际发出的半音数，
        //回报给她之后她才能看出还差多少，而不是一次加一个半音地试。
        public List<float> SegmentMedians;
        //每一段来自练唱清单的第几段(1 起)。用户永远用清单段号说话("把第四段调高")，
        //而 key 的下标是 order 里的位置——8/25 实测 order="4,5,6" 时她把"第四段"
        //当成了第 4 项，抬高了清单第 5 段，用户当场纠正。回报里要用清单段号。
        public List<int> SegmentSourceIndices;
        //整条的有声音高中位数。auto_f0_adjust 关掉之后要自己补上它本来会给的抬升，
        //公式见 Server/SeedVC/vendor/seed-vc/app_svc.py:315：目标中位 − 源中位。
        public float MedianMidi;
    }

    private readonly List<PracticePhrase> m_PracticePhrases = new List<PracticePhrase>();
    private int m_NextPracticePhraseStableId = 0;
    private sealed class QuarantinedSingingCandidate
    {
        public SingingEvidenceSnapshot Evidence;
        public int CandidateId;
        public PracticePhrase Phrase;
        public bool SourceConfirmed;
        public string PlaybackStatus = "ready";
        public string WholeTurnText = "";
        public string SingingSegmentText = "";
        public byte[] RawWavBytes;
        public float SingingProbability;
        public float PitchStability;
        public float MelodicIslandSeconds;
        public float ContentSeconds;
    }
    //来源仍不确定的旋律进入会话级隔离集合，不能直接成为可点唱的 practice 段。
    //旧实现只有一个槽，candidate 2 会覆盖 candidate 1，用户之后说“前两段”时音频
    //已经不可恢复。现在所有候选都保留稳定 ID，直到确认/否认、显式开始新会话，或
    //练唱会话跨重启时按同一陈旧期限清理。
    private readonly List<QuarantinedSingingCandidate> m_QuarantinedSingingCandidates =
        new List<QuarantinedSingingCandidate>();
    //用户已经明确否认的同一份原始录音不能因迟到分析再次变成 pending。
    //这里只保存轻量签名；开始新的练唱会话时一并清空。
    private readonly HashSet<int> m_RejectedQuarantineSignatures = new HashSet<int>();
    private readonly HashSet<int> m_RejectedQuarantineSessionSerials = new HashSet<int>();
    private int m_NextQuarantinedSingingCandidateId = 0;
    //order 里有一部分没认出来、但还有认出来的：照常唱，但要如实说漏了哪些。
    private string m_LastPracticeOrderProblem = "";
    public string ConsumeLastPracticeOrderProblem()
    {
        string value = m_LastPracticeOrderProblem;
        m_LastPracticeOrderProblem = "";
        return value;
    }
    private int m_LastCommittedPracticeSignature = 0;
    private float m_LastPracticeCommitTime = -999f;
    //一首完整歌曲很容易超过 16 个自然句。这里是会话内的素材保留量，不是一次推理
    //要吃下的块数；实际转换会在 ChatSample 里按块排队并及时释放，所以可以适度放宽。
    private const int MaxPracticePhraseCount = 64;
    //旋律相似度分不开不同的歌——实测**跨歌**中位 0.654、同一首歌不同段落中位 0.618，
    //错误合并的那些反而更高。所以这里没有"判对"的阈值可用，只有"判它没意义"的下界：
    //0.654 就是随机拿两首不相干的歌配对能拿到的分。低于它的候选携带的信息量是零，
    //印在感知帧上只会被当成身份依据用。这个数来自实测，不是拍的。
    private const float k_RecallIdentityFloor = 0.654f;
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
    private float m_LastPlayableCandidateRecoveryAudioCropSeconds = 0f;
    private float m_LastPlayableCandidateRecoveryTimelineCropSeconds = 0f;
    private float m_LastPlayableCandidateRecoveryAudioEndSeconds = 0f;
    private float m_LastPlayableCandidateRecoveryTimelineEndSeconds = 0f;
    private float[] m_LastPlayableCandidatePitchTimelineMidi = new float[0];
    private float m_LastPlayableCandidatePitchFrameSeconds = 0.10f;
    private SingingScore m_LastPlayableCandidateScore;
    private string m_LastPlayableCandidateText = "";
    private string m_LastPlayableCandidateLanguage = "";
    private string m_LastPlayableCandidateSingingText = "";
    private string m_LastPlayableCandidateSingingLanguage = "";
    private string m_LastPlayableCandidateSingingReading = "";
    private string m_LastPlayableCandidateSingingReadingSource = "";
    private bool m_LastPlayableCandidateSingingReadingComplete = false;
    private string[] m_LastPlayableCandidateSingingMora;
    private float m_LastPlayableCandidatePerformanceSeconds = 0f;
    private float m_LastPlayableCandidateProbability = 0f;
    private float m_LastPlayableCandidatePitchStability = 0f;
    private float m_LastPlayableCandidateIslandSeconds = 0f;
    private float m_LastPlayableCandidateContentSeconds = 0f;
    private SingingEvidenceSnapshot m_LastPlayableCandidateEvidence;
    //来源证据不能受“能不能播放”支配。每份最终分析都保存这一份原始快照；
    //即使旋律岛不足 3 秒或离线标签为 speech，语义/声学冲突仍能进入 quarantine。
    private sealed class SingingEvidenceSnapshot
    {
        public float TimelineOriginSeconds;
        public int CaptureSessionSerial;
        public byte[] RawWavBytes;
        public float[] PitchTimelineMidi;
        public float FrameSeconds;
        public float AtRealtime;
        public string Text;
        public string SingingText;
        public string Language;
        public float RawSeconds;
        public float CleanStartSeconds;
        public float CleanEndSeconds;
        public float RecoveryStartSeconds;
        public float RecoveryEndSeconds;
        public float SingingProbability;
        public float PitchStability;
        public float MelodicIslandSeconds;
        public float ContentSeconds;
        public float HeadExtraSeconds;
        public float TailExtraSeconds;
        public string HeadExtraText;
        public string TailExtraText;
        public string HeadExtraType;
        public string TailExtraType;
        public float HeadExtraProbability;
        public float TailExtraProbability;
        public bool HeadExtraReviewRequired;
        public bool TailExtraReviewRequired;
        public float HeadExtraMelodicSeconds;
        public float TailExtraMelodicSeconds;
        public float HeadExtraMelodicRatio;
        public float TailExtraMelodicRatio;
        public float HeadExtraLongestMelodicRunSeconds;
        public float TailExtraLongestMelodicRunSeconds;
        public SingingBoundarySubsegment[] HeadExtraSegments;
        public SingingBoundarySubsegment[] TailExtraSegments;
        public float CleanLeadInUnverifiedSeconds;
        public string CleanLeadInEvidenceText;
        public string CleanLeadInEvidenceType;
        public float CleanLeadInEvidenceProbability;
    }

    private static SingingEvidenceSnapshot CloneSingingEvidence(
        SingingEvidenceSnapshot source)
    {
        if (source == null) return null;
        return new SingingEvidenceSnapshot
        {
            CaptureSessionSerial = source.CaptureSessionSerial,
            TimelineOriginSeconds = source.TimelineOriginSeconds,
            RawWavBytes = source.RawWavBytes == null
                ? null : (byte[])source.RawWavBytes.Clone(),
            PitchTimelineMidi = source.PitchTimelineMidi == null
                ? null : (float[])source.PitchTimelineMidi.Clone(),
            FrameSeconds = source.FrameSeconds,
            AtRealtime = source.AtRealtime,
            Text = source.Text,
            SingingText = source.SingingText,
            Language = source.Language,
            RawSeconds = source.RawSeconds,
            CleanStartSeconds = source.CleanStartSeconds,
            CleanEndSeconds = source.CleanEndSeconds,
            RecoveryStartSeconds = source.RecoveryStartSeconds,
            RecoveryEndSeconds = source.RecoveryEndSeconds,
            SingingProbability = source.SingingProbability,
            PitchStability = source.PitchStability,
            MelodicIslandSeconds = source.MelodicIslandSeconds,
            ContentSeconds = source.ContentSeconds,
            HeadExtraSeconds = source.HeadExtraSeconds,
            TailExtraSeconds = source.TailExtraSeconds,
            HeadExtraText = source.HeadExtraText,
            TailExtraText = source.TailExtraText,
            HeadExtraType = source.HeadExtraType,
            TailExtraType = source.TailExtraType,
            HeadExtraProbability = source.HeadExtraProbability,
            TailExtraProbability = source.TailExtraProbability,
            HeadExtraReviewRequired = source.HeadExtraReviewRequired,
            TailExtraReviewRequired = source.TailExtraReviewRequired,
            HeadExtraMelodicSeconds = source.HeadExtraMelodicSeconds,
            TailExtraMelodicSeconds = source.TailExtraMelodicSeconds,
            HeadExtraMelodicRatio = source.HeadExtraMelodicRatio,
            TailExtraMelodicRatio = source.TailExtraMelodicRatio,
            HeadExtraLongestMelodicRunSeconds =
                source.HeadExtraLongestMelodicRunSeconds,
            TailExtraLongestMelodicRunSeconds =
                source.TailExtraLongestMelodicRunSeconds,
            HeadExtraSegments = CloneBoundarySegments(source.HeadExtraSegments),
            TailExtraSegments = CloneBoundarySegments(source.TailExtraSegments),
            CleanLeadInUnverifiedSeconds = source.CleanLeadInUnverifiedSeconds,
            CleanLeadInEvidenceText = source.CleanLeadInEvidenceText,
            CleanLeadInEvidenceType = source.CleanLeadInEvidenceType,
            CleanLeadInEvidenceProbability = source.CleanLeadInEvidenceProbability,
        };
    }

    private static SingingBoundarySubsegment[] CloneBoundarySegments(
        SingingBoundarySubsegment[] source)
    {
        if (source == null || source.Length == 0)
            return new SingingBoundarySubsegment[0];
        var clone = new SingingBoundarySubsegment[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            SingingBoundarySubsegment value = source[i];
            if (value == null) continue;
            clone[i] = new SingingBoundarySubsegment
            {
                start_seconds = value.start_seconds,
                end_seconds = value.end_seconds,
                expanded_start_seconds = value.expanded_start_seconds,
                expanded_end_seconds = value.expanded_end_seconds,
                type = value.type ?? "unknown",
                voiced_ratio = value.voiced_ratio,
                pitch_smooth_ratio = value.pitch_smooth_ratio,
            };
        }
        return clone;
    }
    private SingingEvidenceSnapshot m_LastSingingEvidence;
    //RTSpeechHandler 在每次真实录音开始时清空。录音结束后的最终 ASR 仍属于该会话，
    //所以不能再用“最近 5 秒”判断候选是否过期；否则长歌后的一句口语会把前面的歌声丢掉。
    private bool m_LiveRecordingCandidateSessionActive = false;
    private float m_InputCaptureAt = -1f;
    private float m_LastAnalyzedCaptureAt = -1f;
    private sealed class AnalysisTicket
    {
        public string Id = Guid.NewGuid().ToString("N");
        public bool Cancelled;
        public bool CompletedCapture;
        public bool Detached;
        public UnityWebRequest Request;
    }
    private readonly string m_AnalysisChannel = Guid.NewGuid().ToString("N");
    private readonly List<AnalysisTicket> m_AnalysisTickets = new List<AnalysisTicket>();
    private AnalysisTicket m_PreviewAnalysis;

    public void CancelPreviewAnalysis()
    {
        AnalysisTicket old = m_PreviewAnalysis;
        m_PreviewAnalysis = null;
        CancelAnalysis(old);
    }

    public void CancelInputAnalyses()
    {
        foreach (AnalysisTicket ticket in new List<AnalysisTicket>(m_AnalysisTickets))
        {
            // New speech cancels the old reply, not a recording already handed
            // over for final analysis. Its eventual result enters quarantine.
            if (ticket.CompletedCapture && !ticket.Cancelled) ticket.Detached = true;
            else CancelAnalysis(ticket);
        }
        m_PreviewAnalysis = null;
    }

    public int PendingCompletedCaptureAnalysisCount
    {
        get { return m_AnalysisTickets.Count(t => t.CompletedCapture && t.Detached && !t.Cancelled); }
    }

    private void CancelAnalysis(AnalysisTicket ticket)
    {
        if (ticket == null || ticket.Cancelled) return;
        ticket.Cancelled = true;
        StartCoroutine(ControlAnalysis("cancel", ticket.Id));
        if (ticket.Request != null && !ticket.Request.isDone) ticket.Request.Abort();
    }

    public void PromotePreviewAnalysis()
    {
        if (m_PreviewAnalysis != null && !m_PreviewAnalysis.Cancelled)
        {
            m_PreviewAnalysis.CompletedCapture = true;
            StartCoroutine(ControlAnalysis("promote", m_PreviewAnalysis.Id));
            m_PreviewAnalysis = null;
        }
    }

    private IEnumerator ControlAnalysis(string operation, string id)
    {
        var form = new WWWForm();
        form.AddField("request_id", id);
        using (var request = UnityWebRequest.Post(m_ServerSetting.TrimEnd('/') + "/asr/" + operation, form))
        {
            request.timeout = 3;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                Debug.LogWarning("[ASR/Dispatch] 后端任务控制未生效，请确认已重启 SenseVoice；客户端仍拒绝过期结果。");
        }
    }
    private int m_LiveRecordingCandidateSessionSerial = 0;

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

    /// <summary>
    /// 一次真实麦克风录音的候选边界。预览 ASR、流式复核和最终 ASR 都可更新同一候选；
    /// 下一次真实录音开始才清空，因而长歌不会因为最终口语晚到几秒而失去前面的最佳歌声。
    /// </summary>
    public void BeginLiveRecordingCandidateSession()
    {
        m_CurrentRecordingEvidencePublished = false;
        m_EarlierCaptureTranscripts.Clear();
        m_CurrentCaptureTranscript = "";
        m_InputCaptureAt = Time.realtimeSinceStartup;
        m_LiveRecordingCandidateSessionSerial++;
        ReserveRecordingSequence(m_LiveRecordingCandidateSessionSerial);
        m_LiveRecordingCandidateSessionActive = true;
        m_LastPlayableCandidateAudioBytes = null;
        m_LastPlayableCandidateTime = -999f;
        m_LastPlayableCandidatePitchTimelineMidi = new float[0];
        m_LastPlayableCandidateScore = null;
        m_LastPlayableCandidateSingingMora = null;
        m_LastPlayableCandidatePerformanceSeconds = 0f;
        m_LastPlayableCandidateProbability = 0f;
        m_LastPlayableCandidatePitchStability = 0f;
        m_LastPlayableCandidateIslandSeconds = 0f;
        m_LastPlayableCandidateContentSeconds = 0f;
        m_LastPlayableCandidateRecoveryAudioCropSeconds = 0f;
        m_LastPlayableCandidateRecoveryTimelineCropSeconds = 0f;
        m_LastPlayableCandidateRecoveryAudioEndSeconds = 0f;
        m_LastPlayableCandidateRecoveryTimelineEndSeconds = 0f;
        m_LastPlayableCandidateEvidence = null;
        m_LastSingingEvidence = null;
    }

    public void SetInputCaptureTime(float capturedAt)
    {
        m_InputCaptureAt = capturedAt;
    }

    public bool TryGetCurrentRecordingSingingCandidateFacts(
        out float performanceSeconds,
        out float singingProbability,
        out float pitchStability,
        out float ageSeconds)
    {
        performanceSeconds = m_LastPlayableCandidatePerformanceSeconds;
        singingProbability = m_LastPlayableCandidateProbability;
        pitchStability = m_LastPlayableCandidatePitchStability;
        ageSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - m_LastPlayableCandidateTime);
        bool withinLegacyCallbackWindow = ageSeconds <= 5f;
        return (m_LiveRecordingCandidateSessionActive || withinLegacyCallbackWindow) &&
            m_LastPlayableCandidateAudioBytes != null &&
            m_LastPlayableCandidateAudioBytes.Length > 44 &&
            HasPlayablePitchTimeline(m_LastPlayableCandidatePitchTimelineMidi) &&
            performanceSeconds >= k_MinSingablePerformanceSeconds;
    }

    private static bool IsCrediblePlayableCandidate(
        bool classifiedSinging,
        float singingProbability,
        float pitchStability,
        float islandSeconds,
        float contentSeconds)
    {
        if (classifiedSinging || singingProbability >= k_SingingBandLow) return true;
        float islandRatio = contentSeconds > 0.01f ? islandSeconds / contentSeconds : 0f;
        return islandSeconds >= 4f && pitchStability >= 0.60f && islandRatio >= 0.70f;
    }

    private static bool ShouldReplacePlayableCandidate(
        float existingSeconds,
        float existingQuality,
        float candidateSeconds,
        float candidateQuality)
    {
        if (existingSeconds < k_MinSingablePerformanceSeconds) return true;
        if (candidateSeconds > existingSeconds + 0.35f) return true;
        return Mathf.Abs(candidateSeconds - existingSeconds) <= 0.35f &&
            candidateQuality > existingQuality + 0.02f;
    }

    private void TryStorePlayableCandidate(
        byte[] audioBytes,
        float audioCropSeconds,
        float timelineCropSeconds,
        float scoreCropSeconds,
        float audioEndSeconds,
        float timelineEndSeconds,
        float scoreEndSeconds,
        float recoveryAudioCropSeconds,
        float recoveryTimelineCropSeconds,
        float recoveryAudioEndSeconds,
        float recoveryTimelineEndSeconds)
    {
        if (audioBytes == null || audioBytes.Length <= 44 ||
            !HasPlayablePitchTimeline(LastPitchTimelineMidi)) return;
        float candidateSeconds = MeasurePerformanceSeconds(
            timelineCropSeconds,
            timelineEndSeconds);
        if (candidateSeconds < k_MinSingablePerformanceSeconds ||
            !IsCrediblePlayableCandidate(
                LastIsSinging,
                LastSingingProbability,
                LastPitchStability,
                LastSingingIslandSeconds,
                LastSingingContentSeconds)) return;

        float islandRatio = LastSingingContentSeconds > 0.01f
            ? Mathf.Clamp01(LastSingingIslandSeconds / LastSingingContentSeconds)
            : 0f;
        float quality = Mathf.Clamp01(LastSingingProbability) +
            Mathf.Clamp01(LastPitchStability) + islandRatio;
        float existingRatio = m_LastPlayableCandidateContentSeconds > 0.01f
            ? Mathf.Clamp01(m_LastPlayableCandidateIslandSeconds /
                            m_LastPlayableCandidateContentSeconds)
            : 0f;
        float existingQuality = Mathf.Clamp01(m_LastPlayableCandidateProbability) +
            Mathf.Clamp01(m_LastPlayableCandidatePitchStability) + existingRatio;
        if (!ShouldReplacePlayableCandidate(
                m_LastPlayableCandidatePerformanceSeconds,
                existingQuality,
                candidateSeconds,
                quality)) return;

        m_LastPlayableCandidateAudioBytes = new byte[audioBytes.Length];
        Array.Copy(audioBytes, m_LastPlayableCandidateAudioBytes, audioBytes.Length);
        m_LastPlayableCandidateTime = Time.realtimeSinceStartup;
        m_LastPlayableCandidateAudioCropSeconds = audioCropSeconds;
        m_LastPlayableCandidateTimelineCropSeconds = timelineCropSeconds;
        m_LastPlayableCandidateScoreCropSeconds = scoreCropSeconds;
        m_LastPlayableCandidateAudioEndSeconds = audioEndSeconds;
        m_LastPlayableCandidateTimelineEndSeconds = timelineEndSeconds;
        m_LastPlayableCandidateScoreEndSeconds = scoreEndSeconds;
        m_LastPlayableCandidateRecoveryAudioCropSeconds = recoveryAudioCropSeconds;
        m_LastPlayableCandidateRecoveryTimelineCropSeconds = recoveryTimelineCropSeconds;
        m_LastPlayableCandidateRecoveryAudioEndSeconds = recoveryAudioEndSeconds;
        m_LastPlayableCandidateRecoveryTimelineEndSeconds = recoveryTimelineEndSeconds;
        m_LastPlayableCandidatePitchTimelineMidi =
            (float[])LastPitchTimelineMidi.Clone();
        m_LastPlayableCandidatePitchFrameSeconds = LastPitchTimelineFrameSeconds;
        m_LastPlayableCandidateScore = LastSingingScore == null
            ? null
            : JsonUtility.FromJson<SingingScore>(JsonUtility.ToJson(LastSingingScore));
        m_LastPlayableCandidateText = LastText ?? "";
        m_LastPlayableCandidateLanguage = LastLanguage ?? "";
        m_LastPlayableCandidateSingingText = m_LastResponseSingingText ?? "";
        m_LastPlayableCandidateSingingLanguage = m_LastResponseSingingLanguage ?? "";
        m_LastPlayableCandidateSingingReading = m_LastResponseSingingReading ?? "";
        m_LastPlayableCandidateSingingReadingSource =
            m_LastResponseSingingReadingSource ?? "";
        m_LastPlayableCandidateSingingReadingComplete =
            m_LastResponseSingingReadingComplete;
        m_LastPlayableCandidateSingingMora = m_LastResponseSingingMora == null
            ? null
            : (string[])m_LastResponseSingingMora.Clone();
        m_LastPlayableCandidatePerformanceSeconds = candidateSeconds;
        m_LastPlayableCandidateProbability = LastSingingProbability;
        m_LastPlayableCandidatePitchStability = LastPitchStability;
        m_LastPlayableCandidateIslandSeconds = LastSingingIslandSeconds;
        m_LastPlayableCandidateContentSeconds = LastSingingContentSeconds;
        //音频、旋律、歌词与边界必须作为一个不可拆的候选一起替换。
        //否则 preview 的最佳音频会错误配上 final 的 head/tail 证据。
        m_LastPlayableCandidateEvidence = CloneSingingEvidence(m_LastSingingEvidence);
        Debug.Log($"[SenseVoice/Singing] 本次录音最佳候选更新 " +
                  $"session={m_LiveRecordingCandidateSessionSerial} " +
                  $"duration={candidateSeconds:F2}s p={LastSingingProbability:F2} " +
                  $"stability={LastPitchStability:F2}");
    }

    private void CaptureLatestSingingEvidence(
        byte[] audioBytes,
        float rawSeconds,
        float cleanStartSeconds,
        float cleanEndSeconds,
        float recoveryStartSeconds,
        float recoveryEndSeconds,
        float capturedAt, float timelineOriginSeconds)
    {
        if (audioBytes == null || audioBytes.Length <= 44 ||
            !LastSingingAnalysisAvailable)
            return;
        var snapshot = new SingingEvidenceSnapshot
        {
            CaptureSessionSerial = m_LiveRecordingCandidateSessionSerial,
            TimelineOriginSeconds = timelineOriginSeconds,
            RawWavBytes = (byte[])audioBytes.Clone(),
            PitchTimelineMidi = LastPitchTimelineMidi == null
                ? new float[0] : (float[])LastPitchTimelineMidi.Clone(),
            FrameSeconds = Mathf.Clamp(LastPitchTimelineFrameSeconds, 0.02f, 0.25f),
            AtRealtime = capturedAt >= 0f ? capturedAt : Time.realtimeSinceStartup,
            Text = LastText ?? "",
            SingingText = m_LastResponseSingingText ?? "",
            Language = !string.IsNullOrWhiteSpace(m_LastResponseSingingLanguage)
                ? m_LastResponseSingingLanguage : (LastLanguage ?? ""),
            RawSeconds = rawSeconds > 0f ? rawSeconds : GetWavDurationSeconds(audioBytes),
            CleanStartSeconds = Mathf.Max(0f, cleanStartSeconds),
            CleanEndSeconds = Mathf.Max(0f, cleanEndSeconds),
            RecoveryStartSeconds = Mathf.Max(0f, recoveryStartSeconds),
            RecoveryEndSeconds = Mathf.Max(0f, recoveryEndSeconds),
            SingingProbability = LastSingingProbability,
            PitchStability = LastPitchStability,
            MelodicIslandSeconds = LastSingingIslandSeconds,
            ContentSeconds = LastSingingContentSeconds,
            HeadExtraSeconds = m_LastHeadExtraSeconds,
            TailExtraSeconds = m_LastTailExtraSeconds,
            HeadExtraText = m_LastHeadExtraText ?? "",
            TailExtraText = m_LastTailExtraText ?? "",
            HeadExtraType = m_LastHeadExtraType ?? "none",
            TailExtraType = m_LastTailExtraType ?? "none",
            HeadExtraProbability = m_LastHeadExtraProbability,
            TailExtraProbability = m_LastTailExtraProbability,
            HeadExtraReviewRequired = m_LastHeadExtraReviewRequired,
            TailExtraReviewRequired = m_LastTailExtraReviewRequired,
            HeadExtraMelodicSeconds = m_LastHeadExtraMelodicSeconds,
            TailExtraMelodicSeconds = m_LastTailExtraMelodicSeconds,
            HeadExtraMelodicRatio = m_LastHeadExtraMelodicRatio,
            TailExtraMelodicRatio = m_LastTailExtraMelodicRatio,
            HeadExtraLongestMelodicRunSeconds =
                m_LastHeadExtraLongestMelodicRunSeconds,
            TailExtraLongestMelodicRunSeconds =
                m_LastTailExtraLongestMelodicRunSeconds,
            HeadExtraSegments = CloneBoundarySegments(m_LastHeadExtraSegments),
            TailExtraSegments = CloneBoundarySegments(m_LastTailExtraSegments),
            CleanLeadInUnverifiedSeconds = m_LastCleanLeadInUnverifiedSeconds,
            CleanLeadInEvidenceText = m_LastCleanLeadInEvidenceText ?? "",
            CleanLeadInEvidenceType = m_LastCleanLeadInEvidenceType ?? "none",
            CleanLeadInEvidenceProbability = m_LastCleanLeadInEvidenceProbability,
        };
        m_LastSingingEvidence = snapshot;
        UpdateRetainedRecordingEvidence(snapshot);
        Debug.Log($"[SenseVoice/Evidence] 原始歌唱证据已保存 raw={snapshot.RawSeconds:F2}s " +
                   $"clean={snapshot.CleanStartSeconds:F2}~{snapshot.CleanEndSeconds:F2}s " +
                   $"expanded={snapshot.RecoveryStartSeconds:F2}~{snapshot.RecoveryEndSeconds:F2}s " +
                   $"head_extra={snapshot.HeadExtraSeconds:F2}s/{snapshot.HeadExtraType} " +
                   $"tail_extra={snapshot.TailExtraSeconds:F2}s/{snapshot.TailExtraType} " +
                   $"head_windows={(snapshot.HeadExtraSegments == null ? 0 : snapshot.HeadExtraSegments.Length)} " +
                   $"tail_windows={(snapshot.TailExtraSegments == null ? 0 : snapshot.TailExtraSegments.Length)}；" +
                  $"clean_lead_in_unverified={snapshot.CleanLeadInUnverifiedSeconds:F2}s/" +
                  $"{snapshot.CleanLeadInEvidenceType}；" +
                  "播放资格将在来源确认之外单独判断");
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
            // Empty hypotheses still prove that ASR processed new audio.

            StreamingTranscript transcript = new StreamingTranscript
            {
                Text = response.text ?? "",
                StableText = response.stable_text ?? "",
                UnstableText = response.unstable_text ?? "",
                Language = response.language ?? "",
                Revision = response.revision,
                AudioMs = response.audio_ms,
                WindowStartMs = response.window_start_ms,
                Elapsed = response.elapsed,
                IsSinging = response.is_singing,
                SingingProbability = response.singing_probability,
                PitchStability = response.pitch_stability,
                ActivityAvailable = response.activity_schema >= 1,
                ActivityWindowMs = response.activity_window_ms,
                ActivityRms = response.activity_rms,
                ActivityVadAvailable = response.activity_vad_available,
                ActivitySpeechMs = response.activity_speech_ms,
                ActivitySpeechEndAgeMs = response.activity_speech_end_age_ms,
                ActivityPeriodicity = response.activity_periodicity,
                ActivityVoicedRatio = response.activity_voiced_ratio,
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
        public int WindowStartMs;
        public float Elapsed;
        public bool IsSinging;
        public float SingingProbability;
        public float PitchStability;
        public bool ActivityAvailable;
        public int ActivityWindowMs;
        public float ActivityRms;
        public bool ActivityVadAvailable;
        public int ActivitySpeechMs;
        public int ActivitySpeechEndAgeMs = -1;
        public float ActivityPeriodicity;
        public float ActivityVoicedRatio;
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
        public int window_start_ms = 0;
        public int activity_schema;
        public int activity_window_ms;
        public float activity_rms;
        public bool activity_vad_available;
        public int activity_speech_ms;
        public int activity_speech_end_age_ms = -1;
        public float activity_periodicity;
        public float activity_voiced_ratio;
        public float elapsed = 0f;
        public bool is_singing = false;
        public float singing_probability = 0f;
        public float pitch_stability = 0f;
        public string error = "";
    }

    public override void SpeechToText(AudioClip _clip, Action<string> _callback)
    {
        if (!HasUsableAudioClip(_clip))
        {
            if (_callback != null) _callback("");
            return;
        }
        BeginLiveRecordingCandidateSession();
        byte[] _audioData = WavUtility.FromAudioClip(_clip);
        StartCoroutine(SendAudioData(
            _audioData, _callback, true, false, -1f, -1f, false, false));
    }

    public override void SpeechToText(byte[] _audioData, Action<string> _callback)
    {
        if (!HasUsableWavPayload(_audioData))
        {
            if (_callback != null) _callback("");
            return;
        }
        BeginLiveRecordingCandidateSession();
        StartCoroutine(SendAudioData(
            _audioData, _callback, true, false, -1f, -1f, false, false));
    }

    private static bool HasUsableAudioClip(AudioClip clip)
    {
        return clip != null && clip.samples > 0 && clip.channels > 0 &&
               clip.frequency > 0 && clip.length > 0.005f;
    }

    private static bool HasUsableWavPayload(byte[] audioData)
    {
        //标准 PCM WAV 头通常为 44 字节；只有头、没有任何采样时不能送进 FunASR。
        return audioData != null && audioData.Length > 44;
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
        bool streamingSpokenExitDetected = false,
        bool semanticSpokenLeadIn = false,
        bool speculative = false)
    {
        if (!HasUsableAudioClip(clip))
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
            streamingSpokenExitDetected,
            semanticSpokenLeadIn, speculative));
    }

    /// <summary>
    /// 流式锚点与最终声学岛相差很大时，通常应保住更早的旋律，不能机械追随岛。
    /// 唯一可核查的反向证据是：角色的流式语义已把那段前缀判为普通说话，而且最终
    /// 分析也至少落入可复核歌唱带。1.5 秒过滤两套边界的正常抖动。
    /// </summary>
    private static bool ShouldPreferAcousticIslandForSpokenLeadIn(
        bool semanticSpokenLeadIn,
        bool credibleFinalMelody,
        float acousticCropSeconds,
        float protectedStreamingCropSeconds)
    {
        return semanticSpokenLeadIn && credibleFinalMelody &&
            acousticCropSeconds > 0f && protectedStreamingCropSeconds >= 0f &&
            acousticCropSeconds - protectedStreamingCropSeconds >= 1.5f;
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
        if (!HasUsableWavPayload(audioBytes))
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
                result.SpeakerVoiceprintId = response.speaker_voiceprint_id ?? "";
                result.SpeakerVoiceprintStatus = response.speaker_voiceprint_status ?? "";
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
        bool streamingSpokenExitDetected,
        bool semanticSpokenLeadIn,
        bool speculative = false)
    {
        if (!HasUsableWavPayload(audioBytes))
        {
            if (_callback != null) _callback("");
            yield break;
        }
        int requestSerial = ++m_AsrRequestSerial;
        var ticket = new AnalysisTicket { CompletedCapture = !speculative };
        int captureSessionSerial = m_LiveRecordingCandidateSessionSerial;
        if (speculative) CancelPreviewAnalysis();
        m_AnalysisTickets.Add(ticket);
        if (speculative) m_PreviewAnalysis = ticket;
        float capturedAt = m_InputCaptureAt >= 0f ? m_InputCaptureAt : Time.realtimeSinceStartup;
        stopwatch.Restart();

        WWWForm form = new WWWForm();
        form.AddBinaryData("audio_file", audioBytes, "input.wav", "audio/wav");
        form.AddField("language", m_Language);
        form.AddField("learn_speaker", learnSpeaker ? "true" : "false");
        form.AddField("expect_singing", expectSinging ? "true" : "false");

        form.AddField("request_id", ticket.Id);
        // Supersession is per recording, not per MonoBehaviour. A new capture
        // must not cancel a sealed upload even if /promote arrives out of order.
        form.AddField("channel_id", m_AnalysisChannel + ":" + captureSessionSerial);
        form.AddField("speculative", speculative ? "true" : "false");
        using (UnityWebRequest www = UnityWebRequest.Post(m_SpeechRecognizeURL, form))
        {
            ticket.Request = www;
            float requestStarted = Time.realtimeSinceStartup;
            www.SetRequestHeader("accept", "application/json");

            yield return www.SendWebRequest();

            m_AnalysisTickets.Remove(ticket);
            if (ReferenceEquals(m_PreviewAnalysis, ticket)) m_PreviewAnalysis = null;
            if (ticket.Cancelled || (www.result == UnityWebRequest.Result.Success &&
                JsonUtility.FromJson<Response>(www.downloadHandler.text)?.cancelled == true))
            {
                Debug.Log($"[ASR/Dispatch] 过期分析已取消 request={requestSerial}；不修改本轮感知/记忆");
                _callback?.Invoke("");
                yield break;
            }
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
                    if (ticket.Detached)
                    {
                        ArchiveCompletedCapture(_response, audioBytes, capturedAt, captureSessionSerial);
                        // Never publish the old transcript or awaken its cancelled reply.
                        _callback?.Invoke("");
                        yield break;
                    }
                    if (_response.timing_schema >= 1 && _response.timings != null)
                    {
                        AsrTiming t = _response.timings;
                        Debug.Log($"[ASR/Timing] request={requestSerial} " +
                            $"client={Time.realtimeSinceStartup - requestStarted:F2}s server={t.total:F2}s queue={t.queue_wait:F2}s " +
                            $"decode={t.decode:F2}s vad={t.vad:F2}s quickPitch={t.quick_pitch:F2}s speaker={t.speaker:F2}s " +
                            $"recognizer={t.recognizer:F2}s fullPitch={t.full_pitch:F2}s recovery={t.recovery:F2}s " +
                            $"segment={t.segment_asr:F2}s tail={t.tail_asr:F2}s recall={t.song_recall:F2}s " +
                            $"dump={t.dump:F2}s other={t.other:F2}s");
                    }
                    m_LastCompletedAsrSerial = requestSerial;
                    m_LastAnalyzedCaptureAt = capturedAt;
                    PreserveEarlierCaptureTranscript(_response.text, GetWavDurationSeconds(audioBytes));
                    LastText = _response.text ?? "";
                    m_LastTurnSegments = _response.turn_segments_schema == 1
                        ? _response.turn_segments : null;
                    m_LastWholeTurnText = _response.whole_text ?? "";
                    m_LastSegmentedPrimaryText = LastText;
                    if (HasTimeOrderedTranscript)
                        Debug.Log($"[ASR/Segments] source={_response.transcript_source} " +
                            $"count={m_LastTurnSegments.Length} complete={_response.turn_segments_complete} " +
                            $"review={_response.turn_segments_review_required} " +
                            $"whole=\"{m_LastWholeTurnText}\" primary=\"{LastText}\" " +
                            BuildTimeOrderedTranscriptEvidence(m_LastTurnSegments, m_LastWholeTurnText));
                    LastLanguage = _response.language ?? "";
                    LastEmotion = _response.emotion ?? "";
                    LastEvent = _response.audio_event ?? "";
                    LastSpeakerId = _response.speaker_id ?? "";
                    LastNoSpeech = _response.no_speech;
                    LastSpeakerName = _response.speaker_name ?? "";
                    LastSpeakerKind = _response.speaker_kind ?? "";
                    LastSpeakerStatus = _response.speaker_status ?? "";
                    LastSpeakerVoiceprintId = _response.speaker_voiceprint_id ?? "";
                    LastSpeakerVoiceprintStatus = _response.speaker_voiceprint_status ?? "";
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
                    LastSpeakerSelfConfidence = _response.speaker_self_confidence;
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
                    LastSingingAnalysisAvailable = m_EnableSingingAnalysis &&
                        _response.singing_analysis_available;
                    int acousticTimelineFrames = _response.pitch_timeline_midi != null
                        ? _response.pitch_timeline_midi.Length : 0;
                    LastSingingContentSeconds = LastSingingAnalysisAvailable
                        ? Mathf.Max(0f, _response.pitch_timeline_start_seconds +
                            acousticTimelineFrames * Mathf.Max(
                                0.02f, _response.pitch_timeline_frame_seconds))
                        : 0f;
                    float acousticIslandEnd = _response.singing_end_seconds > 0f
                        ? _response.singing_end_seconds
                        : LastSingingContentSeconds;
                    LastSingingIslandSeconds = LastSingingAnalysisAvailable
                        ? Mathf.Max(0f, acousticIslandEnd - _response.singing_start_seconds)
                        : 0f;
                    LastSingingIslandRatio = LastSingingContentSeconds > 0.01f
                        ? Mathf.Clamp01(
                            LastSingingIslandSeconds / LastSingingContentSeconds)
                        : 0f;

                    //分带现在是感知证据状态：模糊带会交给完整转写 LLM 复核，
                    //但这里仍只记录声学事实，不改服务端 LastIsSinging 原始结论。
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
                        Debug.Log($"[Singing/Band] 离线 prob={_response.singing_probability:F3} " +
                                  $"stab={_response.pitch_stability:F2} → {band} " +
                                  $"(声学证据={LastAcousticMode}; 服务端原判=" +
                                  $"{(_response.is_singing ? "唱" : "说")}) " +
                                  $"岛={LastSingingIslandSeconds:F2}s/" +
                                  $"内容{LastSingingContentSeconds:F2}s " +
                                  $"文本=\"{(LastText ?? "").Trim()}\"");
                    }
                    //说话轮的原话留一份，下一段歌声提交时作为"唱这段之前用户说了什么"。
                    if (!_response.is_singing)
                    {
                        string spoken = (LastText ?? "").Trim();
                        string segmentText = (_response.singing_text ?? "").Trim();
                        if (segmentText.Length > 0)
                        {
                            //整轮文本可能是“说话+歌词”，而 singing_text 是声学岛
                            //的独立转写。只做可验证的字面拆分；对不上时留空，
                            //不把整轮歌词冒充成“唱前说话”。
                            spoken = ExtractPrecedingSpeechFromMixedTranscript(
                                spoken, segmentText);
                            m_LastSpokenTranscript = spoken.Length >= 4 ? spoken : "";
                        }
                        //太短的应答（「嗯」「好」）说明不了任何事，留着反而占地方。
                        else if (spoken.Length >= 4)
                        {
                            m_LastSpokenTranscript = spoken;
                        }
                    }
                    //每一份响应都要重置：调用方问的是「刚刚这一轮裁了多少头」，
                    //沿用上一轮的值会让没有演唱的轮次继承一个大裁剪量。
                    m_LastResponseAudioCropSeconds = 0f;
                    m_LastResponseAudioTailDropSeconds = 0f;
                    m_LastCropLeadInSeconds = 0f;
                    m_LastCleanLeadInUnverifiedSeconds = 0f;
                    m_LastCleanLeadInEvidenceText = "";
                    m_LastCleanLeadInEvidenceType = "none";
                    m_LastCleanLeadInEvidenceProbability = 0f;
                    m_LastResponseSingingTailText = _response.singing_tail_text ?? "";
                    //每轮必赋值：沿用上一轮会让这一次的歌声配上别的歌的回忆
                    m_LastSongRecall = _response.song_recall;
                    m_LastResponseSingingText = _response.singing_text ?? "";
                    m_LastHeadExtraText = _response.singing_head_extra_text ?? "";
                    m_LastTailExtraText = _response.singing_tail_extra_text ?? "";
                    m_LastHeadExtraType = string.IsNullOrWhiteSpace(
                            _response.singing_head_extra_type)
                        ? "none" : _response.singing_head_extra_type.Trim().ToLowerInvariant();
                    m_LastTailExtraType = string.IsNullOrWhiteSpace(
                            _response.singing_tail_extra_type)
                        ? "none" : _response.singing_tail_extra_type.Trim().ToLowerInvariant();
                    m_LastHeadExtraProbability =
                        Mathf.Clamp01(_response.singing_head_extra_probability);
                    m_LastTailExtraProbability =
                        Mathf.Clamp01(_response.singing_tail_extra_probability);
                    m_LastHeadExtraReviewRequired =
                        _response.singing_head_extra_review_required;
                    m_LastTailExtraReviewRequired =
                        _response.singing_tail_extra_review_required;
                    m_LastHeadExtraMelodicSeconds = Mathf.Max(
                        0f, _response.singing_head_extra_melodic_seconds);
                    m_LastTailExtraMelodicSeconds = Mathf.Max(
                        0f, _response.singing_tail_extra_melodic_seconds);
                    m_LastHeadExtraMelodicRatio = Mathf.Clamp01(
                        _response.singing_head_extra_melodic_ratio);
                    m_LastTailExtraMelodicRatio = Mathf.Clamp01(
                        _response.singing_tail_extra_melodic_ratio);
                    m_LastHeadExtraLongestMelodicRunSeconds = Mathf.Max(
                        0f, _response.singing_head_extra_longest_melodic_run_seconds);
                    m_LastTailExtraLongestMelodicRunSeconds = Mathf.Max(
                        0f, _response.singing_tail_extra_longest_melodic_run_seconds);
                    m_LastHeadExtraSegments = CloneBoundarySegments(
                        _response.singing_head_extra_segments);
                    m_LastTailExtraSegments = CloneBoundarySegments(
                        _response.singing_tail_extra_segments);
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
                    bool semanticLeadInIslandOverride = false;
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
                    bool credibleFinalMelody = LastIsSinging ||
                        LastSingingProbability >= k_SingingBandLow;
                    if (ShouldPreferAcousticIslandForSpokenLeadIn(
                            semanticSpokenLeadIn,
                            credibleFinalMelody,
                            acousticAudioCropSeconds,
                            protectedStreamingCropSeconds))
                    {
                        //最终声学岛前只留 0.20s 呼吸余量。下面会以同一个绝对时刻
                        //同步裁剪音高时间线，避免把说话音频配到后面的歌词/旋律上。
                        audioCropSeconds = Mathf.Max(0f, acousticAudioCropSeconds - 0.20f);
                        semanticLeadInIslandOverride = true;
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
                    bool alignCropToIsland = semanticLeadInIslandOverride;

                    // 音频与音高时间线必须描述同一段。时间线只从 pitch_timeline_start_seconds
                    // 开始（服务端只为歌声那部分建时间线），如果音频裁得比它还靠前，多出来的
                    // 那截就没有旋律与之对应——8/9 实测保守分支把 audioCrop 归零，音频留了
                    // 19.16s、时间线只有 9.90s，排队时按 melody=9.9s 记账，实际播出去 19.10s，
                    // 前面 9.3 秒的说话被原样复读。
                    float timelineStartAbsolute = _response.audio_content_start_seconds +
                        _response.pitch_timeline_start_seconds;
                    bool clampedToTimeline = audioCropSeconds < timelineStartAbsolute - 0.05f;
                    if (clampedToTimeline) audioCropSeconds = timelineStartAbsolute;

                    //两者都是原始 WAV 的绝对坐标。旧代码拿 content-relative 的
                    //singing_start_seconds 去减 absolute audioCrop，在 post-VAD 有偏移时
                    //会凭空放大/缩小这段。该差值表示 acoustic clean 之前有多少音频
                    //被流式保护纳入了实际 clean；它可能是呼吸，也可能是口语。
                    m_LastCropLeadInSeconds = Mathf.Max(
                        0f, acousticAudioCropSeconds - audioCropSeconds);
                    m_LastCleanLeadInUnverifiedSeconds = m_LastCropLeadInSeconds;
                    if (m_LastCleanLeadInUnverifiedSeconds > 0.10f)
                    {
                        m_LastCleanLeadInEvidenceText = m_LastHeadExtraText ?? "";
                        //这里只知道该前缀邻接 head_extra，并没有独立分析这个精确子区间。
                        //保留转写和概率作为线索，但不能把相邻区间的标签冒充硬事实。
                        m_LastCleanLeadInEvidenceType = "uncertain";
                        m_LastCleanLeadInEvidenceProbability = m_LastHeadExtraProbability;
                    }

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
                    //扩展边界是另一份可恢复素材，不覆盖上面的干净边界。它由服务端把
                    //锚点附近被换气/不稳定音切开的旋律岛重新连起来；只有角色明确选择
                    //capture="expanded" 才会播放。
                    float recoveryAudioCropSeconds = audioCropSeconds;
                    float recoveryAudioEndSeconds = audioEndSeconds;
                    if (_response.singing_recovery_start_seconds >= 0f)
                    {
                        float absoluteRecoveryStart = _response.audio_content_start_seconds +
                            _response.singing_recovery_start_seconds;
                        if (absoluteRecoveryStart < recoveryAudioCropSeconds)
                            recoveryAudioCropSeconds = absoluteRecoveryStart;
                    }
                    if (_response.singing_recovery_end_seconds > 0f)
                    {
                        float absoluteRecoveryEnd = _response.audio_content_start_seconds +
                            _response.singing_recovery_end_seconds;
                        if (recoveryAudioEndSeconds <= 0f ||
                            absoluteRecoveryEnd > recoveryAudioEndSeconds)
                            recoveryAudioEndSeconds = absoluteRecoveryEnd;
                    }
                    //音高时间线之前没有可执行旋律，扩展音频也不能越过它。
                    recoveryAudioCropSeconds = Mathf.Max(
                        recoveryAudioCropSeconds, timelineStartAbsolute);
                    if (rawAudioSeconds > 0f &&
                        recoveryAudioEndSeconds >= rawAudioSeconds - 0.45f)
                        recoveryAudioEndSeconds = 0f;
                    float recoveryContentCropSeconds = Mathf.Max(
                        0f, recoveryAudioCropSeconds - _response.audio_content_start_seconds);
                    float recoveryTimelineCropSeconds = Mathf.Max(
                        0f, recoveryContentCropSeconds - _response.pitch_timeline_start_seconds);
                    float recoveryContentEndSeconds = recoveryAudioEndSeconds > 0f
                        ? Mathf.Max(0f, recoveryAudioEndSeconds - _response.audio_content_start_seconds)
                        : 0f;
                    float recoveryTimelineEndSeconds = recoveryContentEndSeconds > 0f
                        ? Mathf.Max(0f, recoveryContentEndSeconds - _response.pitch_timeline_start_seconds)
                        : 0f;
                    //服务端对长录音只在尾部 45 秒建立音高/乐谱数组，但边界时间戳已经
                    //换算回整段坐标。音频照绝对坐标裁；乐谱必须扣掉分析窗口偏移。
                    float scoreWindowOffset = Mathf.Max(
                        0f, _response.singing_score_window_offset_seconds);
                    float scoreCropSeconds = Mathf.Max(
                        0f, croppedContentSeconds - scoreWindowOffset);
                    float scoreEndSeconds = contentEndSeconds > 0f
                        ? Mathf.Max(0f, contentEndSeconds - scoreWindowOffset)
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
                                  $"语义说话前缀改按岛={semanticLeadInIslandOverride} " +
                                  $"起点一致={onsetsAgree} " +
                                  $"timeline={timelineCropSeconds:F2}s " +
                                  $"对齐时间线={clampedToTimeline}" +
                                  (clampedToTimeline ? $"(→{timelineStartAbsolute:F2}s)" : ""));
                        Debug.Log($"[SenseVoice/Singing] tail rawEnd={_response.singing_end_seconds:F2}s " +
                                  $"contentStart={_response.audio_content_start_seconds:F2}s " +
                                  $"applied={audioEndSeconds:F2}s " +
                                  $"timeline={timelineEndSeconds:F2}s");
                        string recoveryTailLabel = recoveryAudioEndSeconds > 0f
                            ? recoveryAudioEndSeconds.ToString("F2") + "s"
                            : "raw-end";
                        string recoveryTimelineEndLabel = recoveryTimelineEndSeconds > 0f
                            ? recoveryTimelineEndSeconds.ToString("F2")
                            : "end";
                        Debug.Log($"[SenseVoice/Singing] recovery " +
                                  $"head={recoveryAudioCropSeconds:F2}s " +
                                  $"tail={recoveryTailLabel} " +
                                  $"timeline={recoveryTimelineCropSeconds:F2}~" +
                                  recoveryTimelineEndLabel);
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
                        Debug.Log($"[Singing/LeadIn] 裁剪起点={audioCropSeconds:F2}s " +
                                  $"声学岛绝对起点={acousticAudioCropSeconds:F2}s " +
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

                    float cleanAbsoluteEnd = audioEndSeconds > 0f
                        ? audioEndSeconds : rawAudioSeconds;
                    float recoveryAbsoluteEnd = recoveryAudioEndSeconds > 0f
                        ? recoveryAudioEndSeconds : rawAudioSeconds;
                    m_LastHeadExtraSeconds = Mathf.Max(
                        0f, audioCropSeconds - recoveryAudioCropSeconds);
                    m_LastTailExtraSeconds = Mathf.Max(
                        0f, recoveryAbsoluteEnd - cleanAbsoluteEnd);
                    float serverHeadStart = _response.audio_content_start_seconds +
                        _response.singing_head_extra_start_seconds;
                    float serverHeadEnd = _response.audio_content_start_seconds +
                        _response.singing_head_extra_end_seconds;
                    float serverTailStart = _response.audio_content_start_seconds +
                        _response.singing_tail_extra_start_seconds;
                    float serverTailEnd = _response.audio_content_start_seconds +
                        _response.singing_tail_extra_end_seconds;
                    bool headEvidenceAligned = m_LastHeadExtraSeconds <= 0.10f ||
                        (Mathf.Abs(serverHeadStart - recoveryAudioCropSeconds) <= 0.15f &&
                         Mathf.Abs(serverHeadEnd - audioCropSeconds) <= 0.15f);
                    bool tailEvidenceAligned = m_LastTailExtraSeconds <= 0.10f ||
                        (Mathf.Abs(serverTailStart - cleanAbsoluteEnd) <= 0.15f &&
                         Mathf.Abs(serverTailEnd - recoveryAbsoluteEnd) <= 0.15f);
                    if (!headEvidenceAligned)
                    {
                        Debug.LogWarning($"[Singing/Boundary] 服务端 head 证据区间 " +
                                         $"{serverHeadStart:F2}~{serverHeadEnd:F2}s 与最终采用 " +
                                         $"{recoveryAudioCropSeconds:F2}~{audioCropSeconds:F2}s 不同；" +
                                         "类型降为 uncertain，不把相邻区间转写冒充为实际边界");
                        m_LastHeadExtraType = "uncertain";
                        m_LastHeadExtraText = "";
                        m_LastHeadExtraProbability = 0f;
                        m_LastHeadExtraReviewRequired = false;
                        m_LastHeadExtraMelodicSeconds = 0f;
                        m_LastHeadExtraMelodicRatio = 0f;
                        m_LastHeadExtraLongestMelodicRunSeconds = 0f;
                        m_LastHeadExtraSegments = new SingingBoundarySubsegment[0];
                    }
                    if (!tailEvidenceAligned)
                    {
                        Debug.LogWarning($"[Singing/Boundary] 服务端 tail 证据区间 " +
                                         $"{serverTailStart:F2}~{serverTailEnd:F2}s 与最终采用 " +
                                         $"{cleanAbsoluteEnd:F2}~{recoveryAbsoluteEnd:F2}s 不同；" +
                                         "类型降为 uncertain，不把相邻区间转写冒充为实际边界");
                        m_LastTailExtraType = "uncertain";
                        m_LastTailExtraText = "";
                        m_LastTailExtraProbability = 0f;
                        m_LastTailExtraReviewRequired = false;
                        m_LastTailExtraMelodicSeconds = 0f;
                        m_LastTailExtraMelodicRatio = 0f;
                        m_LastTailExtraLongestMelodicRunSeconds = 0f;
                        m_LastTailExtraSegments = new SingingBoundarySubsegment[0];
                    }
                    CaptureLatestSingingEvidence(
                        audioBytes,
                        rawAudioSeconds,
                        audioCropSeconds,
                        cleanAbsoluteEnd,
                        recoveryAudioCropSeconds,
                        recoveryAbsoluteEnd,
                        capturedAt, timelineStartAbsolute);

                    bool hasPlayablePitch = HasPlayablePitchTimeline(LastPitchTimelineMidi);
                    //说话出现在旋律之前或之后，只决定这一轮应先回应口语，不能抹掉
                    //中间实际录到的歌声。始终保存服务端已经裁净的旋律岛候选；绝不
                    //把未裁剪的整轮混合音频拿去回唱。
                    if (hasPlayablePitch)
                    {
                        TryStorePlayableCandidate(
                            audioBytes,
                            audioCropSeconds,
                            timelineCropSeconds,
                            scoreCropSeconds,
                            audioEndSeconds,
                            timelineEndSeconds,
                            scoreEndSeconds,
                            recoveryAudioCropSeconds,
                            recoveryTimelineCropSeconds,
                            recoveryAudioEndSeconds,
                            recoveryTimelineEndSeconds);
                    }

                    if (LastIsSinging)
                    {
                        CacheLastSingingPerformance(
                            audioBytes,
                            audioCropSeconds,
                            timelineCropSeconds,
                            scoreCropSeconds,
                            audioEndSeconds,
                            timelineEndSeconds,
                            scoreEndSeconds,
                            recoveryAudioCropSeconds,
                            recoveryTimelineCropSeconds,
                            recoveryAudioEndSeconds,
                            recoveryTimelineEndSeconds);
                    }
                    if (LastIsSinging && endsWithSpokenSingingExit)
                    {
                        Debug.Log("[SenseVoice/Singing] 检出末尾口语退出语义；" +
                                  "本轮整段不自动回唱，但干净歌唱岛已缓存为最近素材");
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
                                  $"voiceprint={LastSpeakerVoiceprintId}/{LastSpeakerVoiceprintStatus} " +
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
        return BuildLastPerceivedText(true);
    }

    /// <summary>
    /// uncertain/冲突帧可保留说话人、情绪和原始转写，同时不抢先附加
    /// “已确认演唱”的前缀。
    /// </summary>
    public string BuildLastPerceivedText(bool includeSingingConclusion)
    {
        string perceivedText = string.IsNullOrWhiteSpace(LastText)
            ? "（没有识别出歌词的哼唱片段）"
            : LastText;
        return (m_InjectSpeakerPrefix ? BuildSpeakerPrefix() : "")
            + (m_InjectMetaPrefix ? BuildMetaPrefix() : "")
            + (includeSingingConclusion && LastIsSinging ? BuildSingingPrefix() : "")
            + perceivedText
            + (HasTimeOrderedTranscript ? " " + BuildLastMixedTurnEvidence() : "");
    }

    /// <summary>
    /// 把同一录音里的整轮文字通道与歌唱岛独立文字通道并列交给角色。
    /// 这里只报告通道、时间顺序与素材事实，不替角色判定用户是在唱、朗读还是说话。
    /// </summary>
    public string BuildLastMixedTurnEvidence()
    {
        string earlier = m_EarlierCaptureTranscripts.Count == 0 ? "" :
            "\n[同一录音的较早 ASR 观测：不是额外发言，也不表示最终结果必然更准确。" +
            "可用于补漏和判断冲突，不要重复拼接；有歧义时可以询问。\n" +
            string.Join("\n", m_EarlierCaptureTranscripts) + "]";
        if (HasTimeOrderedTranscript)
            return BuildTimeOrderedTranscriptEvidence(m_LastTurnSegments, m_LastWholeTurnText) + earlier;
        return BuildMixedTurnEvidence(
            LastText,
            LastSegmentLyrics,
            string.IsNullOrWhiteSpace(m_LastResponseSingingLanguage)
                ? LastLanguage
                : m_LastResponseSingingLanguage,
            LastResponseAudioCropSeconds,
            LastSingingIslandSeconds,
            LastResponseAudioTailDropSeconds,
            LastSingingTailText) + earlier;
    }

    private void PreserveEarlierCaptureTranscript(string incoming, float seconds)
    {
        incoming = incoming ?? "";
        if (!string.IsNullOrWhiteSpace(m_CurrentCaptureTranscript) && incoming != m_CurrentCaptureTranscript)
        {
            m_EarlierCaptureTranscripts.Add($"覆盖录音前 {m_CurrentCaptureTranscriptSeconds:F2}s：" + m_CurrentCaptureTranscript);
            if (m_EarlierCaptureTranscripts.Count > 2) m_EarlierCaptureTranscripts.RemoveAt(0);
        }
        m_CurrentCaptureTranscript = incoming;
        m_CurrentCaptureTranscriptSeconds = seconds;
    }

    /// <summary>
    /// 仅当独立歌唱通道补充了整轮 ASR 没有的信息，才把分轨事实交给角色。
    /// 这不是歌唱分类器：不读取 singing probability，也不要求角色必须追问。
    /// </summary>
    public string BuildLastInformativeMixedTurnEvidence(
        bool allowTimelineOnlyEvidence,
        out string reason)
    {
        if (HasTimeOrderedTranscript)
        {
            reason = "time_ordered_independent_asr";
            return BuildLastMixedTurnEvidence();
        }
        reason = ClassifyInformativeMixedTurnEvidence(
            LastText,
            LastSegmentLyrics,
            LastResponseAudioCropSeconds,
            LastSingingIslandSeconds,
            LastResponseAudioTailDropSeconds,
            LastSingingTailText,
            allowTimelineOnlyEvidence);
        return string.IsNullOrEmpty(reason) ? "" : BuildLastMixedTurnEvidence();
    }

    private const float k_MixedEvidenceBoundarySeconds = 0.5f;

    private static string BuildTimeOrderedTranscriptEvidence(
        TurnTranscriptSegment[] segments, string wholeText)
    {
        if (segments == null || segments.Length == 0) return "";
        var sb = new StringBuilder("[同轮分轨观测；以下为同一录音按时间独立ASR的主语义，时间基于去除首尾静音后的内容音频；" +
            "这是已经录完的一轮，不是接下来才会发生的计划。请同时理解唱前约定、实际歌声和唱后请求；" +
            "较早流式预判可能只听到开头，不能代替下面完整时序。是否回应、行动或询问仍由你判断。" +
            "type仅是声学候选，uncertain不等于没有唱歌。尾段可能包含新的用户请求，请结合完整时序理解。" +
            "alternatives为边界重叠窗口的备选，不是重复发言，不要直接拼接；空文本/failed表示未识别，不能推断没有声音。" +
            "confidence_available=false表示ASR没有提供可校准置信度。存在歧义时可以先询问，也可以结合语境自行判断。\n");
        foreach (var segment in segments)
            if (segment != null) sb.AppendLine(JsonUtility.ToJson(segment));
        sb.Append("整轮ASR仅作补漏/冲突参考，不能覆盖分段内容：");
        sb.Append(JsonUtility.ToJson(new WholeTurnObservation { text = wholeText ?? "" }));
        sb.Append(']');
        return sb.ToString();
    }

    [Serializable]
    private class WholeTurnObservation { public string text; }

    [Serializable]
    private class TurnTranscriptAlternative
    {
        public string source, text, language, audio_event, error;
        public float start_seconds, end_seconds;
    }

    [Serializable]
    private class TurnTranscriptSegment
    {
        public int id;
        public float start_seconds, end_seconds;
        public string region, type, type_source, text, language, status, error;
        public bool confidence_available, review_required;
        public TurnTranscriptAlternative[] alternatives;
    }

    /// <summary>
    /// 返回非空原因表示分轨通道确实新增了可核查事实。0.5 秒只用于过滤 ASR/裁剪
    /// 边界抖动，不参与“唱/说”判断，也不会改变任何音频缓存或动作权限。
    /// </summary>
    private static string ClassifyInformativeMixedTurnEvidence(
        string wholeTranscript,
        string singingTranscript,
        float leadSeconds,
        float singingSeconds,
        float tailSeconds,
        string tailTranscript,
        bool allowTimelineOnlyEvidence)
    {
        string whole = NormalizeEvidenceComparisonText(wholeTranscript);
        string singing = NormalizeEvidenceComparisonText(singingTranscript);
        string tail = NormalizeEvidenceComparisonText(tailTranscript);
        bool hasSingingText = singing.Length >= 2;
        bool hasTailText = tail.Length >= 2;
        bool singingAddsText = hasSingingText &&
            (whole.Length == 0 || !whole.Contains(singing));
        bool tailAddsText = hasTailText &&
            (whole.Length == 0 || !whole.Contains(tail));
        if (singingAddsText) return "singing_text_adds_information";
        if (tailAddsText) return "tail_text_adds_information";

        //纯时序差异本身不能证明发生过歌唱。普通说话的 VAD 边界和 pitch 探针也会
        //稳定地产生几秒“旋律岛”；只有本轮另有歌唱复核依据时，才允许把 lead/island/tail
        //时序作为补充事实。独立歌词/尾部文字在上面仍可直接放行，因为它们确实新增了内容。
        if (!allowTimelineOnlyEvidence) return "";

        bool meaningfulBoundary =
            leadSeconds >= k_MixedEvidenceBoundarySeconds ||
            tailSeconds >= k_MixedEvidenceBoundarySeconds;
        bool channelsDiffer = hasSingingText && whole.Length >= 2 &&
            !string.Equals(whole, singing, StringComparison.Ordinal);
        if (meaningfulBoundary && channelsDiffer)
            return "temporal_channel_split";
        if (meaningfulBoundary && singingSeconds >= 3f &&
            (whole.Length >= 2 || hasTailText))
            return "timed_mixed_audio";
        return "";
    }

    private static string NormalizeEvidenceComparisonText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var normalized = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            normalized.Append(char.ToLowerInvariant(ch));
        }
        return normalized.ToString();
    }

    private static string BuildMixedTurnEvidence(
        string wholeTranscript,
        string singingTranscript,
        string singingLanguage,
        float leadSeconds,
        float singingSeconds,
        float tailSeconds,
        string tailTranscript)
    {
        string whole = CompactEvidenceText(wholeTranscript, 220);
        string singing = CompactEvidenceText(singingTranscript, 220);
        string tail = CompactEvidenceText(tailTranscript, 120);
        leadSeconds = Mathf.Max(0f, leadSeconds);
        singingSeconds = Mathf.Max(0f, singingSeconds);
        tailSeconds = Mathf.Max(0f, tailSeconds);
        if (singing.Length == 0 && singingSeconds <= 0.01f) return "";

        var sb = new StringBuilder(
            "[同轮分轨观测；各通道来自本轮同一份录音，按时间并列，不互相覆盖：");
        sb.Append("时序=");
        if (leadSeconds > 0.01f)
            sb.Append($"前置音频约{leadSeconds:F1}秒 → ");
        sb.Append(singingSeconds > 0.01f
            ? $"可播放旋律岛约{singingSeconds:F1}秒"
            : "检测到歌唱岛文字通道");
        if (tailSeconds > 0.01f)
            sb.Append($" → 尾部音频约{tailSeconds:F1}秒");
        if (whole.Length > 0)
            sb.Append($"；整轮ASR文字通道=\"{whole}\"");
        if (singing.Length > 0)
        {
            string language = string.IsNullOrWhiteSpace(singingLanguage)
                ? "未知语言"
                : singingLanguage.Trim();
            sb.Append($"；歌唱岛独立ASR文字通道（{language}，歌词仅为推测）=\"{singing}\"");
        }
        if (tail.Length > 0)
            sb.Append($"；尾部独立ASR文字通道=\"{tail}\"");
        sb.Append("。整轮ASR没有写出歌唱岛歌词，不等于这一轮没有发生歌声；" +
                  "这些只是可核查的感知事实，不是程序替你作出的语义结论。]");
        return sb.ToString();
    }

    private static string CompactEvidenceText(string raw, int maxCharacters)
    {
        string value = (raw ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        while (value.Contains("  ")) value = value.Replace("  ", " ");
        int max = Mathf.Max(16, maxCharacters);
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }

    private bool CachePlayableCandidateForCurrentResult()
    {
        if (!TryGetCurrentRecordingSingingCandidateFacts(
                out float _, out float _, out float _, out float _)) return false;

        float[] savedTimeline = LastPitchTimelineMidi;
        float savedFrameSeconds = LastPitchTimelineFrameSeconds;
        SingingScore savedScore = LastSingingScore;
        string savedText = LastText;
        string savedLanguage = LastLanguage;
        string savedSingingText = m_LastResponseSingingText;
        string savedSingingLanguage = m_LastResponseSingingLanguage;
        string savedReading = m_LastResponseSingingReading;
        string savedReadingSource = m_LastResponseSingingReadingSource;
        bool savedReadingComplete = m_LastResponseSingingReadingComplete;
        string[] savedMora = m_LastResponseSingingMora;
        SingingEvidenceSnapshot savedEvidence = m_LastSingingEvidence;
        try
        {
            LastPitchTimelineMidi = m_LastPlayableCandidatePitchTimelineMidi;
            LastPitchTimelineFrameSeconds = m_LastPlayableCandidatePitchFrameSeconds;
            LastSingingScore = m_LastPlayableCandidateScore;
            LastText = m_LastPlayableCandidateText;
            LastLanguage = m_LastPlayableCandidateLanguage;
            m_LastResponseSingingText = m_LastPlayableCandidateSingingText;
            m_LastResponseSingingLanguage = m_LastPlayableCandidateSingingLanguage;
            m_LastResponseSingingReading = m_LastPlayableCandidateSingingReading;
            m_LastResponseSingingReadingSource =
                m_LastPlayableCandidateSingingReadingSource;
            m_LastResponseSingingReadingComplete =
                m_LastPlayableCandidateSingingReadingComplete;
            m_LastResponseSingingMora = m_LastPlayableCandidateSingingMora;
            m_LastSingingEvidence = CloneSingingEvidence(
                m_LastPlayableCandidateEvidence);
            CacheLastSingingPerformance(
                m_LastPlayableCandidateAudioBytes,
                m_LastPlayableCandidateAudioCropSeconds,
                m_LastPlayableCandidateTimelineCropSeconds,
                m_LastPlayableCandidateScoreCropSeconds,
                m_LastPlayableCandidateAudioEndSeconds,
                m_LastPlayableCandidateTimelineEndSeconds,
                m_LastPlayableCandidateScoreEndSeconds,
                m_LastPlayableCandidateRecoveryAudioCropSeconds,
                m_LastPlayableCandidateRecoveryTimelineCropSeconds,
                m_LastPlayableCandidateRecoveryAudioEndSeconds,
                m_LastPlayableCandidateRecoveryTimelineEndSeconds);
        }
        finally
        {
            LastPitchTimelineMidi = savedTimeline;
            LastPitchTimelineFrameSeconds = savedFrameSeconds;
            LastSingingScore = savedScore;
            LastText = savedText;
            LastLanguage = savedLanguage;
            m_LastResponseSingingText = savedSingingText;
            m_LastResponseSingingLanguage = savedSingingLanguage;
            m_LastResponseSingingReading = savedReading;
            m_LastResponseSingingReadingSource = savedReadingSource;
            m_LastResponseSingingReadingComplete = savedReadingComplete;
            m_LastResponseSingingMora = savedMora;
            m_LastSingingEvidence = savedEvidence;
        }
        return m_LastSingingCacheSerial == m_LastCompletedAsrSerial &&
            HasFreshSingingAudio() &&
            HasPlayablePitchTimeline(m_LastSingingPerformanceMidi);
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
        //流式 0.52~0.55 的真唱"这条救援通道，纯亏。而且当时日志里提升点总共
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
        Debug.Log($"[Singing/Band] 提升点 streamProb={streamingProbability:F3} " +
                  $"→ {DescribeSingingBand(streamingProbability)}  " +
                  $"(判据 prob>=0.55: {streamingProbability >= 0.55f}) " +
                  $"streamStab={streamingPitchStability:F2}(仅观测, " +
                  $"旧判据下会={streamingPitchStability >= 0.52f}) " +
                  $"离线prob={LastSingingProbability:F2}(判说话) " +
                  $"文本=\"{(LastText ?? "").Trim()}\"");
        bool freshCandidate = TryGetCurrentRecordingSingingCandidateFacts(
            out float promotableSeconds,
            out float _,
            out float _,
            out float _);
        if (!offlineAllowsPromotion)
        {
            Debug.Log($"[Singing/Band] 提升被拒：离线 prob={LastSingingProbability:F3} " +
                      $"落在低区(<{k_SingingBandLow:F2})，不接受流式 {streamingProbability:F2} 的推翻");
            return false;
        }
        if (!strongStreamingEvidence || !freshCandidate ||
            !HasPlayablePitchTimeline(m_LastPlayableCandidatePitchTimelineMidi))
            return false;
        //提升是拿流式证据推翻离线的“判说”，素材再短就什么都撑不住了。8/11
        //「那你试着唱出来啊」正是从这里进去的：离线 0.42 判说，流式把它提成歌唱，
        //缓存下 1.4s / 9 字，随后她把这句问话本身回哼了出去。长度不达标就连
        //LastIsSinging 也不置真，否则这一轮会被当成“她唱过”而缓存里却是上一段。
        if (promotableSeconds < k_MinSingablePerformanceSeconds)
        {
            Debug.Log($"[Singing/Band] 提升被拒：可唱素材只有 {promotableSeconds:F2}s，" +
                      $"短于 {k_MinSingablePerformanceSeconds:F1}s");
            return false;
        }

        LastIsSinging = true;
        LastSingingProbability = Mathf.Max(LastSingingProbability, streamingProbability);
        LastPitchStability = Mathf.Max(LastPitchStability, streamingPitchStability);
        if (!CachePlayableCandidateForCurrentResult()) return false;
        Debug.Log($"[SenseVoice/Singing] 最终判定由流式证据恢复为歌唱 " +
                  $"prob={LastSingingProbability:F2} stability={LastPitchStability:F2} " +
                  $"timeline={m_LastSingingPerformanceMidi.Length}");
        return true;
    }

    /// <summary>
    /// 声学判为说话、而语义判断认为可能在唱时，只暂存这一轮已经由服务端产出的
    /// 可播放候选，不把 LastIsSinging 改成 true。这样程序不会替角色下结论，
    /// 但角色向用户确认后仍有真实录音和音高时间线可用。
    /// </summary>
    public bool PreserveLastPlayableCandidateForConfirmation(out float performanceSeconds)
    {
        performanceSeconds = 0f;
        if (!m_EnableSingingAnalysis || LastNoSpeech || LastIsSinging ||
            EndsWithSpokenSingingExit(LastText))
            return false;

        bool freshCandidate = TryGetCurrentRecordingSingingCandidateFacts(
            out performanceSeconds,
            out float _,
            out float _,
            out float _);
        if (!freshCandidate ||
            !HasPlayablePitchTimeline(m_LastPlayableCandidatePitchTimelineMidi))
            return false;

        if (performanceSeconds < k_MinSingablePerformanceSeconds)
        {
            Debug.Log($"[SenseVoice/Singing] 语义/声学冲突候选只有 {performanceSeconds:F2}s，" +
                      $"短于 {k_MinSingablePerformanceSeconds:F1}s；只报告冲突，不保存为可回唱素材");
            return false;
        }

        if (!CachePlayableCandidateForCurrentResult()) return false;
        bool preserved = m_LastSingingCacheSerial == m_LastCompletedAsrSerial &&
            HasFreshSingingAudio() &&
            HasPlayablePitchTimeline(m_LastSingingPerformanceMidi);
        if (preserved)
        {
            Debug.Log($"[SenseVoice/Singing] 已暂存语义/声学冲突候选 " +
                      $"duration={performanceSeconds:F2}s；未改写最终模态");
        }
        return preserved;
    }

    /// <summary>
    /// 保留混合录音中已经测量并裁净的歌唱岛。前置或尾部口语决定整轮对话如何回应，
    /// 但不应删除两者之间真实录到的旋律。本方法不把整轮改判成歌唱，只建立
    /// recent_turn 可播放素材。
    /// </summary>
    public bool PreserveLastPlayableSingingIslandFromMixedTurn(
        out float performanceSeconds)
    {
        performanceSeconds = 0f;
        if (!m_EnableSingingAnalysis || LastNoSpeech) return false;

        if (HasCurrentSingingPerformanceCandidate())
        {
            performanceSeconds = m_LastSingingPerformanceMidi.Length *
                Mathf.Max(0.02f, m_LastSingingPerformanceFrameSeconds);
            return true;
        }

        bool freshCandidate = TryGetCurrentRecordingSingingCandidateFacts(
            out performanceSeconds,
            out float _,
            out float _,
            out float _);
        if (!freshCandidate ||
            !HasPlayablePitchTimeline(m_LastPlayableCandidatePitchTimelineMidi) ||
            performanceSeconds < k_MinSingablePerformanceSeconds)
            return false;

        bool preserved = CachePlayableCandidateForCurrentResult() &&
            HasCurrentSingingPerformanceCandidate();
        if (preserved)
        {
            Debug.Log($"[SenseVoice/Singing] 混合轮干净歌唱岛已保留 " +
                      $"duration={performanceSeconds:F2}s；整轮对话模态仍交给口语");
        }
        return preserved;
    }

    /// <summary>当前最终 ASR 这一轮是否确实留下了成对的录音与旋律。</summary>
    public bool HasCurrentSingingPerformanceCandidate()
    {
        bool sameAnalysis = m_LastSingingCacheSerial == m_LastCompletedAsrSerial;
        bool sameCapture = m_LastSingingCacheCaptureSessionSerial > 0 &&
            m_LastSingingCacheCaptureSessionSerial ==
                m_LiveRecordingCandidateSessionSerial;
        return (sameAnalysis || sameCapture) &&
            HasFreshSingingAudio() &&
            HasPlayablePitchTimeline(m_LastSingingPerformanceMidi) &&
            Time.realtimeSinceStartup - m_LastSingingPerformanceTime <=
                m_SingingAudioRetentionSeconds;
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
        RevokeCurrentSingingPlaybackAlias(
            "本轮歌唱分类降级为说话，恢复上一段有效演唱");
        LastSingingSummary = "singing classification rejected as speech: " +
            (reason ?? "speech evidence");
        Debug.Log("[SenseVoice/Singing] 本轮歌唱分类已降级为普通说话，不作为可回唱歌声: " +
                  (reason ?? "speech evidence"));
    }

    /// <summary>
    /// 把当前 ASR 轮临时发布到 recent_turn 的可播放别名撤回，并恢复上一段素材。
    /// quarantine 可以复制这份音频作为隔离证据，但来源确认前绝不能让普通回唱工具
    /// 从 recent_turn 绕过隔离区直接播放它。
    /// </summary>
    private bool RevokeCurrentSingingPlaybackAlias(string reason)
    {
        bool sameAnalysis = m_LastSingingCacheSerial == m_LastCompletedAsrSerial;
        bool sameCapture = m_LastSingingCacheCaptureSessionSerial > 0 &&
            m_LastSingingCacheCaptureSessionSerial ==
                m_LiveRecordingCandidateSessionSerial;
        if (!sameAnalysis && !sameCapture) return false;
        m_LastSingingAudioBytes = m_RollbackSingingAudioBytes;
        m_LastSingingRecoveryAudioBytes = m_RollbackSingingRecoveryAudioBytes;
        m_LastSingingLyrics = m_RollbackSingingLyrics;
        m_LastSingingAudioTime = m_RollbackSingingAudioTime;
        m_LastSingingPerformanceTime = m_RollbackSingingPerformanceTime;
        m_LastSingingPerformanceMidi = m_RollbackSingingPerformanceMidi ??
            new float[0];
        m_LastSingingRecoveryPerformanceMidi =
            m_RollbackSingingRecoveryPerformanceMidi ?? new float[0];
        m_LastSingingPerformanceFrameSeconds =
            m_RollbackSingingPerformanceFrameSeconds;
        m_LastSingingPerformanceLanguage =
            m_RollbackSingingPerformanceLanguage ?? "";
        m_LastSingingPerformanceScore = m_RollbackSingingPerformanceScore;
        m_LastSingingCacheEvidence = CloneSingingEvidence(
            m_RollbackSingingCacheEvidence);
        m_LastSingingCacheCaptureSessionSerial =
            m_RollbackSingingCacheCaptureSessionSerial;
        m_LastSingingCacheSerial = -1;
        Debug.Log("[SenseVoice/Singing] 已撤回本轮 recent_turn 可播放别名：" +
                  (reason ?? "来源仍待确认"));
        return true;
    }

    /// <summary>
    /// Compatibility wrapper for the explicit “singing then spoken tail” safety path.
    /// </summary>
    public void DowngradeLastMixedSingingToSpeech(string reason)
    {
        //混合轮不是“歌唱误判”：对话终点是说话，独立裁出的歌唱岛也同时为真。
        //这里若复用普通误判回滚，会让【说话→唱歌→说话】整段消失，并迫使下一轮
        //“唱刚才那段”错误地退到长期曲库。
        LastIsSinging = false;
        LastSingingSummary = "mixed singing-to-speech; clean singing island preserved: " +
            (reason ?? "tail speech");
        Debug.Log("[SenseVoice/Singing] 混合轮按口语结束处理；" +
                  "保留本轮干净歌唱岛作为最近素材: " +
                  (reason ?? "tail speech"));
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

    private static string ExtractPrecedingSpeechFromMixedTranscript(
        string fullText,
        string singingText)
    {
        string full = (fullText ?? "").Trim();
        string singing = (singingText ?? "").Trim();
        if (full.Length == 0 || singing.Length == 0) return "";
        int index = full.LastIndexOf(singing, StringComparison.OrdinalIgnoreCase);
        if (index <= 0) return "";
        return full.Substring(0, index).Trim();
    }

    private void CacheLastSingingPerformance(
        byte[] audioBytes,
        float audioCropSeconds = 0f,
        float timelineCropSeconds = 0f,
        float scoreCropSeconds = 0f,
        float audioEndSeconds = 0f,
        float timelineEndSeconds = 0f,
        float scoreEndSeconds = 0f,
        float recoveryAudioCropSeconds = 0f,
        float recoveryTimelineCropSeconds = 0f,
        float recoveryAudioEndSeconds = 0f,
        float recoveryTimelineEndSeconds = 0f)
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
        int recoveryTimelineStart;
        int recoveryTimelineEnd;
        bool recoveryWindowUnplayable;
        float[] recoveryPerformance = ResolvePerformanceTimeline(
            recoveryTimelineCropSeconds,
            recoveryTimelineEndSeconds,
            out recoveryTimelineStart,
            out recoveryTimelineEnd,
            out recoveryWindowUnplayable);
        if (performance != null &&
            performance.Length * frameSeconds < k_MinSingablePerformanceSeconds)
        {
            Debug.Log("[SenseVoice/Singing] 本轮可唱素材只有 " +
                      $"{performance.Length * frameSeconds:F2}s（{performance.Length} 帧），" +
                      $"短于 {k_MinSingablePerformanceSeconds:F1}s，不作为可回唱歌声，" +
                      "保留旧素材，不将它认作本轮新歌；新证据交由候选确认/恢复。");
            return;
        }

        int captureSessionSerial = m_LastSingingEvidence != null &&
            m_LastSingingEvidence.CaptureSessionSerial > 0
                ? m_LastSingingEvidence.CaptureSessionSerial
                : m_LiveRecordingCandidateSessionSerial;
        bool replacesSameCapture = captureSessionSerial > 0 &&
            captureSessionSerial == m_LastSingingCacheCaptureSessionSerial;
        //preview 与 final 是同一录音的两个版本。后一个版本替换前一个时，
        //回滚点仍须是本次录音之前的可信素材，不能被同轮 preview 覆盖。
        if (!replacesSameCapture)
        {
            m_RollbackSingingAudioBytes = m_LastSingingAudioBytes;
            m_RollbackSingingRecoveryAudioBytes = m_LastSingingRecoveryAudioBytes;
            m_RollbackSingingLyrics = m_LastSingingLyrics;
            m_RollbackSingingAudioTime = m_LastSingingAudioTime;
            m_RollbackSingingPerformanceTime = m_LastSingingPerformanceTime;
            m_RollbackSingingPerformanceMidi = m_LastSingingPerformanceMidi;
            m_RollbackSingingRecoveryPerformanceMidi =
                m_LastSingingRecoveryPerformanceMidi;
            m_RollbackSingingPerformanceFrameSeconds =
                m_LastSingingPerformanceFrameSeconds;
            m_RollbackSingingPerformanceLanguage =
                m_LastSingingPerformanceLanguage;
            m_RollbackSingingPerformanceScore =
                m_LastSingingPerformanceScore;
            m_RollbackSingingCacheEvidence = CloneSingingEvidence(
                m_LastSingingCacheEvidence);
            m_RollbackSingingCacheCaptureSessionSerial =
                m_LastSingingCacheCaptureSessionSerial;
        }
        m_LastSingingCacheSerial = m_LastCompletedAsrSerial;
        m_LastSingingCacheCaptureSessionSerial = captureSessionSerial;
        m_LastSingingCacheEvidence = CloneSingingEvidence(m_LastSingingEvidence);

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
            float cleanSeconds = GetWavDurationSeconds(m_LastSingingAudioBytes);
            float recoveryActualCrop;
            float recoveryActualEnd;
            byte[] recoveryAudio = TrimWavWindow(
                audioBytes,
                recoveryAudioCropSeconds,
                recoveryAudioEndSeconds,
                out recoveryActualCrop,
                out recoveryActualEnd);
            float recoverySeconds = GetWavDurationSeconds(recoveryAudio);
            if (recoveryPerformance != null &&
                recoveryPerformance.Length * frameSeconds >= k_MinSingablePerformanceSeconds &&
                recoverySeconds > cleanSeconds + 0.20f)
            {
                m_LastSingingRecoveryAudioBytes = recoveryAudio;
                m_LastSingingRecoveryPerformanceMidi = recoveryPerformance;
                string recoveryTailLabel = recoveryActualEnd > 0f
                    ? recoveryActualEnd.ToString("F2") + "s"
                    : "raw-end";
                Debug.Log($"[SenseVoice/Singing] 可恢复扩展素材 " +
                          $"clean={cleanSeconds:F2}s expanded={recoverySeconds:F2}s " +
                          $"head={recoveryActualCrop:F2}s " +
                          $"tail={recoveryTailLabel}");
            }
            else
            {
                m_LastSingingRecoveryAudioBytes = null;
                m_LastSingingRecoveryPerformanceMidi = new float[0];
            }
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

    /// <summary>
    /// 角色侧的受限声纹管理入口。服务端只开放 review/auto/merge/move/detach/undo，
    /// 不允许角色删除身份或声纹。
    /// </summary>
    public void ManageSpeakers(
        string action,
        string sourceId,
        string targetId,
        string voiceprintId,
        string operationId,
        string displayName,
        Action<SpeakerManagementResult> callback)
    {
        StartCoroutine(ManageSpeakersRequest(
            action,
            sourceId,
            targetId,
            voiceprintId,
            operationId,
            displayName,
            callback));
    }

    private IEnumerator ManageSpeakersRequest(
        string action,
        string sourceId,
        string targetId,
        string voiceprintId,
        string operationId,
        string displayName,
        Action<SpeakerManagementResult> callback)
    {
        WWWForm form = new WWWForm();
        form.AddField("action", (action ?? "review").Trim());
        form.AddField("source_id", (sourceId ?? "").Trim());
        form.AddField("target_id", (targetId ?? "").Trim());
        form.AddField("voiceprint_id", (voiceprintId ?? "").Trim());
        form.AddField("operation_id", (operationId ?? "").Trim());
        form.AddField("display_name", (displayName ?? "").Trim());

        using (UnityWebRequest www = UnityWebRequest.Post(
                   m_ServerSetting.TrimEnd('/') + "/speakers/manage", form))
        {
            yield return www.SendWebRequest();
            SpeakerManagementResult result = null;
            try
            {
                result = JsonUtility.FromJson<SpeakerManagementResult>(
                    www.downloadHandler.text);
            }
            catch (Exception exception)
            {
                if (m_VerboseLog)
                    Debug.LogWarning("[Speaker/Manage] 响应解析失败: " + exception.Message);
            }
            if (result == null) result = new SpeakerManagementResult();
            if (www.result != UnityWebRequest.Result.Success)
            {
                result.ok = false;
                if (string.IsNullOrWhiteSpace(result.error))
                    result.error = string.IsNullOrWhiteSpace(www.downloadHandler.text)
                        ? www.error
                        : www.downloadHandler.text;
            }
            if (m_VerboseLog)
                Debug.Log($"[Speaker/Manage] action={action} ok={result.ok} " +
                          $"error={result.error}");
            if (callback != null) callback(result);
        }
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

    /// <summary>
    /// 读取本机歌曲记忆的实时快照。服务端返回全部条目，筛选和分页留在 Unity 端，
    /// 避免把 catalog_path/audio_dir 等内部路径暴露给角色，也不需要为只读查询改服务端。
    /// </summary>
    public void InspectSongCatalog(
        string query,
        int offset,
        int limit,
        bool includeUnnamed,
        Action<SongCatalogInspectionResult> callback)
    {
        StartCoroutine(SendSongCatalogInspection(
            query, offset, limit, includeUnnamed, callback));
    }

    private IEnumerator SendSongCatalogInspection(
        string query,
        int offset,
        int limit,
        bool includeUnnamed,
        Action<SongCatalogInspectionResult> callback)
    {
        using (UnityWebRequest www = UnityWebRequest.Get(m_SongCatalogURL))
        {
            www.SetRequestHeader("accept", "application/json");
            yield return www.SendWebRequest();
            if (www.result != UnityWebRequest.Result.Success)
            {
                if (callback != null)
                {
                    callback(new SongCatalogInspectionResult
                    {
                        Ok = false,
                        Error = www.error + " / " + www.downloadHandler.text,
                    });
                }
                yield break;
            }

            SongCatalogResponse response = null;
            try { response = JsonUtility.FromJson<SongCatalogResponse>(www.downloadHandler.text); }
            catch (Exception e)
            {
                Debug.LogWarning("[SongCatalog] JSON 解析失败: " + e.Message);
            }
            if (response == null || !response.ok)
            {
                if (callback != null)
                {
                    callback(new SongCatalogInspectionResult
                    {
                        Ok = false,
                        Error = response == null ? "invalid response" : response.error,
                    });
                }
                yield break;
            }

            SongCatalogEntry[] all = response.songs ?? new SongCatalogEntry[0];
            int namedCount = 0;
            int unnamedCount = 0;
            var exactTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filtered = new List<SongCatalogEntry>();
            string cleanQuery = (query ?? "").Trim();
            for (int i = 0; i < all.Length; i++)
            {
                SongCatalogEntry entry = all[i];
                if (entry == null) continue;
                bool named = entry.named && !string.IsNullOrWhiteSpace(entry.title);
                if (named)
                {
                    namedCount++;
                    exactTitles.Add(entry.title.Trim());
                }
                else unnamedCount++;

                // 默认列表不展开未命名条目，但精确拿 song_id 查询时仍应能找到它。
                // 否则工具虽然宣称支持按 ID 自查，实际上必须先猜到 include_unnamed=true。
                bool queryMatchesId = cleanQuery.Length > 0 &&
                    CatalogFieldContains(entry.song_id, cleanQuery);
                if (!named && !includeUnnamed && !queryMatchesId) continue;
                if (cleanQuery.Length > 0 &&
                    !queryMatchesId &&
                    !CatalogFieldContains(entry.title, cleanQuery) &&
                    !CatalogFieldContains(entry.artist, cleanQuery) &&
                    !CatalogFieldContains(entry.display_name, cleanQuery))
                    continue;
                filtered.Add(entry);
            }

            filtered.Sort((left, right) =>
            {
                string a = left != null ? left.display_name ?? left.title ?? "" : "";
                string b = right != null ? right.display_name ?? right.title ?? "" : "";
                int byName = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                return string.Compare(
                    left != null ? left.song_id ?? "" : "",
                    right != null ? right.song_id ?? "" : "",
                    StringComparison.OrdinalIgnoreCase);
            });

            int safeLimit = Mathf.Clamp(limit, 1, 50);
            int safeOffset = Mathf.Clamp(offset, 0, filtered.Count);
            int end = Mathf.Min(filtered.Count, safeOffset + safeLimit);
            var page = new List<SongCatalogEntry>(end - safeOffset);
            for (int i = safeOffset; i < end; i++) page.Add(filtered[i]);

            if (callback != null)
            {
                callback(new SongCatalogInspectionResult
                {
                    Ok = true,
                    Query = cleanQuery,
                    IncludeUnnamed = includeUnnamed,
                    TotalEntries = all.Length,
                    NamedEntries = namedCount,
                    UnnamedEntries = unnamedCount,
                    UniqueExactTitleGroups = exactTitles.Count,
                    MatchedEntries = filtered.Count,
                    Offset = safeOffset,
                    Limit = safeLimit,
                    HasMore = end < filtered.Count,
                    NextOffset = end,
                    Entries = page.ToArray(),
                });
            }
        }
    }

    private static bool CatalogFieldContains(string field, string query)
    {
        return !string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(query) &&
            field.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public bool HasFreshSingingAudio()
    {
        return string.IsNullOrEmpty(RecentSingingMaterialConflict) && m_LastSingingAudioBytes != null &&
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
                ClipRef = m_PracticePhrases[i].ClipRef,
                RecordingSequence = EnsureRecordingSequence(m_PracticePhrases[i]),
                RecordingTranscript = m_PracticePhrases[i].RecordingEvidence?.Text ?? "",
                RawSeconds = m_PracticePhrases[i].RecordingEvidence?.RawSeconds ?? 0f,
                CleanStartSeconds = m_PracticePhrases[i].RecordingEvidence?.CleanStartSeconds ?? 0f,
                CleanEndSeconds = m_PracticePhrases[i].RecordingEvidence?.CleanEndSeconds ?? 0f,
                ExpandedStartSeconds = m_PracticePhrases[i].RecordingEvidence?.RecoveryStartSeconds ?? 0f,
                ExpandedEndSeconds = m_PracticePhrases[i].RecordingEvidence?.RecoveryEndSeconds ?? 0f,
                Index = i + 1,
                StableId = m_PracticePhrases[i].StableId,
                Revision = m_PracticePhrases[i].Revision,
                ActiveCapture = m_PracticePhrases[i].ActiveCapture ?? "clean",
                ActiveTrimHeadSeconds = m_PracticePhrases[i].ActiveTrimHeadSeconds,
                ActiveTrimTailSeconds = m_PracticePhrases[i].ActiveTrimTailSeconds,
                Lyrics = m_PracticePhrases[i].Lyrics ?? "",
                Seconds = m_PracticePhrases[i].Seconds,
                RecoverySeconds = m_PracticePhrases[i].RecoveryWavBytes != null
                    ? GetWavDurationSeconds(m_PracticePhrases[i].RecoveryWavBytes)
                    : m_PracticePhrases[i].Seconds,
                OriginalCleanSeconds = m_PracticePhrases[i].OriginalCleanSeconds > 0f
                    ? m_PracticePhrases[i].OriginalCleanSeconds
                    : GetWavDurationSeconds(m_PracticePhrases[i].SourceCleanWavBytes ??
                                            m_PracticePhrases[i].WavBytes),
                OriginalExpandedSeconds = m_PracticePhrases[i].OriginalExpandedSeconds > 0f
                    ? m_PracticePhrases[i].OriginalExpandedSeconds
                    : GetWavDurationSeconds(m_PracticePhrases[i].SourceExpandedWavBytes ??
                                            m_PracticePhrases[i].RecoveryWavBytes ??
                                            m_PracticePhrases[i].SourceCleanWavBytes ??
                                            m_PracticePhrases[i].WavBytes),
                Language = m_PracticePhrases[i].Language ?? "",
                SongId = m_PracticePhrases[i].SongId ?? "",
                SongName = m_PracticePhrases[i].SongName ?? "",
                AgoSeconds = Mathf.Max(
                    0f, Time.realtimeSinceStartup - m_PracticePhrases[i].AtRealtime),
                ConfirmedAgoSeconds = Mathf.Max(
                    0f, Time.realtimeSinceStartup - m_PracticePhrases[i].ConfirmedAtRealtime),
                HasExpandedCapture = m_PracticePhrases[i].RecoveryWavBytes != null &&
                    m_PracticePhrases[i].RecoveryWavBytes.Length > 44 &&
                    HasPlayablePitchTimeline(
                        m_PracticePhrases[i].RecoveryMidiTimeline),
                PitchCenterMidi = MedianVoicedPitch(m_PracticePhrases[i].MidiTimeline),
                FirstStablePitchMidi = FirstStableVoicedPitch(
                    m_PracticePhrases[i].MidiTimeline),
                PitchRangeLowMidi = VoicedPitchPercentile(
                    m_PracticePhrases[i].MidiTimeline, 0.10f),
                PitchRangeHighMidi = VoicedPitchPercentile(
                    m_PracticePhrases[i].MidiTimeline, 0.90f),
                PrecedingSpeech = m_PracticePhrases[i].PrecedingSpeech ?? "",
                PendingConfirmation = m_PracticePhrases[i].PendingConfirmation,
                OriginCandidateId = m_PracticePhrases[i].OriginCandidateId,
                HeadExtraSeconds = m_PracticePhrases[i].HeadExtraSeconds,
                TailExtraSeconds = m_PracticePhrases[i].TailExtraSeconds,
                HeadExtraText = m_PracticePhrases[i].HeadExtraText ?? "",
                TailExtraText = m_PracticePhrases[i].TailExtraText ?? "",
                HeadExtraType = m_PracticePhrases[i].HeadExtraType ?? "none",
                TailExtraType = m_PracticePhrases[i].TailExtraType ?? "none",
                HeadExtraProbability = m_PracticePhrases[i].HeadExtraProbability,
                TailExtraProbability = m_PracticePhrases[i].TailExtraProbability,
                HeadExtraReviewRequired = m_PracticePhrases[i].HeadExtraReviewRequired,
                TailExtraReviewRequired = m_PracticePhrases[i].TailExtraReviewRequired,
                HeadExtraMelodicSeconds = m_PracticePhrases[i].HeadExtraMelodicSeconds,
                TailExtraMelodicSeconds = m_PracticePhrases[i].TailExtraMelodicSeconds,
                HeadExtraMelodicRatio = m_PracticePhrases[i].HeadExtraMelodicRatio,
                TailExtraMelodicRatio = m_PracticePhrases[i].TailExtraMelodicRatio,
                HeadExtraLongestMelodicRunSeconds =
                    m_PracticePhrases[i].HeadExtraLongestMelodicRunSeconds,
                TailExtraLongestMelodicRunSeconds =
                    m_PracticePhrases[i].TailExtraLongestMelodicRunSeconds,
                HeadExtraSegments = CloneBoundarySegments(
                    m_PracticePhrases[i].HeadExtraSegments),
                TailExtraSegments = CloneBoundarySegments(
                    m_PracticePhrases[i].TailExtraSegments),
                CleanLeadInUnverifiedSeconds =
                    m_PracticePhrases[i].CleanLeadInUnverifiedSeconds,
                CleanLeadInEvidenceText =
                    m_PracticePhrases[i].CleanLeadInEvidenceText ?? "",
                CleanLeadInEvidenceType =
                    m_PracticePhrases[i].CleanLeadInEvidenceType ?? "none",
                CleanLeadInEvidenceProbability =
                    m_PracticePhrases[i].CleanLeadInEvidenceProbability,
            });
        }
        AnnotateCaptureOrder(list);
        AnnotateTakeGroups(list);
        AnnotatePitchBases(list);
        return list;
    }

    public string RecentSingingMaterialConflict
    {
        get
        {
            var newer = m_QuarantinedSingingCandidates
                .Where(c => c.Phrase != null && c.Phrase.CaptureSessionSerial > m_LastSingingCacheCaptureSessionSerial)
                .OrderByDescending(c => c.Phrase.AtRealtime).FirstOrDefault();
            if (newer == null)
            {
                var latest = m_PracticePhrases.Where(p => p.CaptureSessionSerial > m_LastSingingCacheCaptureSessionSerial)
                    .OrderByDescending(p => p.AtRealtime).FirstOrDefault();
                return latest == null ? "" : $"最新确认录音为 stable:{latest.StableId}；旧 recent_turn 不是它。请明确使用该 stable 引用。";
            }
            return $"最新歌唱证据是 candidate:{newer.CandidateId}，source=" +
                (newer.SourceConfirmed ? "confirmed_user" : "pending") +
                $"，playback={newer.PlaybackStatus}；旧 recent_turn 缓存不是这份录音，不能自动替代。" +
                "可确认/恢复该候选，或明确选择旧 stable:N；不确定时可以询问。";
        }
    }

    private static void AnnotateCaptureOrder(List<PracticePhraseInfo> list)
    {
        var captured = new List<PracticePhraseInfo>(list);
        captured.Sort((a, b) => {
            int time = b.AgoSeconds.CompareTo(a.AgoSeconds);
            return time != 0 ? time : a.StableId.CompareTo(b.StableId);
        });
        for (int i = 0; i < captured.Count; i++) captured[i].CaptureOrder =
            captured[i].RecordingSequence > 0 ? captured[i].RecordingSequence : i + 1;
    }

    /// <summary>
    /// 把数值音高标成人和 LLM 都能直接使用的标准音名。
    /// 中心音高、首个稳定音和主要音域是三种不同事实，不再混叫“起调”。
    /// </summary>
    private static void AnnotatePitchBases(List<PracticePhraseInfo> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            list[i].PitchCenterNote = list[i].PitchCenterMidi > 0f
                ? $"{MidiToNoteName(list[i].PitchCenterMidi)}" +
                  $"({Mathf.RoundToInt(list[i].PitchCenterMidi)})"
                : "";
            list[i].FirstStablePitchNote = list[i].FirstStablePitchMidi > 0f
                ? MidiToNoteName(list[i].FirstStablePitchMidi)
                : "";
            list[i].PitchRangeNote = list[i].PitchRangeLowMidi > 0f &&
                                     list[i].PitchRangeHighMidi > 0f
                ? MidiToNoteName(list[i].PitchRangeLowMidi) + "～" +
                  MidiToNoteName(list[i].PitchRangeHighMidi)
                : "";
            list[i].PitchMedianMidi = list[i].PitchCenterMidi;
            list[i].PitchBaseNote = list[i].PitchCenterNote;
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
    /// 第一个至少连续三帧、窗口跨度不超过两个半音的音高。它比“第一个非零帧”更不容易
    /// 把吸气、起音毛刺或倍频错误当作旋律首音；找不到时回退到第一个有声帧。
    /// </summary>
    private static float FirstStableVoicedPitch(float[] timeline)
    {
        if (timeline == null || timeline.Length == 0) return 0f;
        float fallback = 0f;
        for (int i = 0; i < timeline.Length; i++)
        {
            float value = timeline[i];
            if (value <= 1f || float.IsNaN(value) || float.IsInfinity(value)) continue;
            if (fallback <= 0f) fallback = value;
            if (i + 2 >= timeline.Length) continue;
            float b = timeline[i + 1];
            float c = timeline[i + 2];
            if (b <= 1f || c <= 1f || float.IsNaN(b) || float.IsNaN(c) ||
                float.IsInfinity(b) || float.IsInfinity(c))
                continue;
            float low = Mathf.Min(value, Mathf.Min(b, c));
            float high = Mathf.Max(value, Mathf.Max(b, c));
            if (high - low <= 2f) return value + b + c - low - high;
        }
        return fallback;
    }

    /// <summary>有声帧的稳健分位数；用于排除少量八度误检后展示主要音域。</summary>
    private static float VoicedPitchPercentile(float[] timeline, float percentile)
    {
        if (timeline == null || timeline.Length == 0) return 0f;
        var voiced = new List<float>(timeline.Length);
        foreach (float value in timeline)
        {
            if (value > 1f && !float.IsNaN(value) && !float.IsInfinity(value))
                voiced.Add(value);
        }
        if (voiced.Count == 0) return 0f;
        voiced.Sort();
        int index = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Clamp01(percentile) * (voiced.Count - 1)),
            0,
            voiced.Count - 1);
        return voiced[index];
    }

    /// <summary>
    /// 标出"同一句的第几遍"。判据用歌词——用户重唱同一句时歌词高度一致，
    /// 而不同段落的歌词差别很大。刻意不用旋律相似度：实测它连不同的歌都分不开
    /// (组内中位 0.701 / 跨组 0.675)，拿来分"同一句的两遍"更不可能。
    /// 这里只做展示分组，不影响任何合成行为——选哪一遍仍然由用户/她用 order 决定。
    /// </summary>
    private static void AnnotateTakeGroups(List<PracticePhraseInfo> list)
    {
        // Group display follows actual capture order, while order="..." indices
        // remain the original list's identifiers (confirmation may have arrived late).
        list = new List<PracticePhraseInfo>(list);
        list.Sort((a, b) => a.CaptureOrder.CompareTo(b.CaptureOrder));
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
    /// <summary>
    /// 解析 order。每一项可以是段号("3")，也可以是那一段的歌词片段("卢浮宫")。
    /// </summary>
    /// <remarks>
    /// 段号是纯机器编号，人和她都记不住——8/25 实测用户说"把第四段调高"，
    /// 而 order="4,5,6" 时 key 的第 1 项才是清单第 4 段，她抬错了一段；
    /// 用户自己也说"聊着聊着我自己都不记得段落情况了"。
    /// 用歌词指段是双方本来就在用的说法，不需要任何一边记编号。
    /// 段号仍然可用，两种写法可以混着写。
    /// </remarks>
    private List<int> ParsePracticeOrder(string order)
    {
        return ParsePracticeOrder(order, out _);
    }

    private List<int> ParsePracticeOrder(string order, out string failure)
    {
        failure = "";
        if (string.IsNullOrWhiteSpace(order)) return null;
        var picked = new List<int>();
        var problems = new List<string>();
        //分隔符只认**半角** , ; |。歌词里的标点是全角的（「谁对谁错，爱对爱少」），
        //日文 ASR 的输出还是空格分隔的（「初めて の ルーブル は」）——
        //拿全角逗号、顿号或空格断项会把一句歌词切碎。半角逗号只会是她打的分隔符。
        //纯段号的老写法用的分隔符更宽松，单独处理。
        //引号括起来的整体永远算一项，给"歌词里真的有半角逗号"留后路。
        foreach (string piece in SplitOrderItems(order, LooksLikeNumericOrder(order)))
        {
            string t = piece.Trim().Trim('"', '\'', '“', '”', '「', '」').Trim();
            if (t.Length == 0) continue;
            var stableMatch = System.Text.RegularExpressions.Regex.Match(
                t, @"^(?:stable|id)\s*:\s*(?<id>\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (stableMatch.Success &&
                int.TryParse(stableMatch.Groups["id"].Value, out int stableId))
            {
                int stableIndex = FindPracticeIndexByStableId(stableId);
                if (stableIndex < 0)
                    problems.Add($"stable_id={stableId} 在当前练唱会话中不存在");
                else
                    picked.Add(stableIndex);
                continue;
            }
            var candidateMatch = System.Text.RegularExpressions.Regex.Match(
                t, @"^candidate\s*:\s*(?<id>\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (candidateMatch.Success &&
                int.TryParse(candidateMatch.Groups["id"].Value, out int candidateId))
            {
                int candidateIndex = -1;
                for (int i = 0; i < m_PracticePhrases.Count; i++)
                    if (m_PracticePhrases[i].OriginCandidateId == candidateId)
                    {
                        candidateIndex = i;
                        break;
                    }
                if (candidateIndex < 0)
                    problems.Add($"candidate_id={candidateId} 尚未确认成可播放练唱素材");
                else
                    picked.Add(candidateIndex);
                continue;
            }
            if (int.TryParse(t, out int n))
            {
                if (n < 1 || n > m_PracticePhrases.Count)
                {
                    problems.Add($"段号 {n} 超出范围（当前只有 {m_PracticePhrases.Count} 段）");
                    continue;
                }
                picked.Add(n - 1);
                continue;
            }
            var hits = MatchPracticePhrasesByLyric(t);
            if (hits.Count == 0)
            {
                problems.Add($"「{t}」在练唱会话里找不到对应的段");
            }
            else if (hits.Count > 1)
            {
                //同一句被唱过好几遍时歌词必然多重命中。不替她挑，让她用段号定。
                problems.Add(
                    $"「{t}」同时命中第 {string.Join("、", hits.ConvertAll(x => (x + 1).ToString()))} 段" +
                    "（同一句的多遍），请改用段号指明要哪一遍");
            }
            else if (!picked.Contains(hits[0]))
            {
                picked.Add(hits[0]);
            }
        }
        if (problems.Count > 0)
            failure = string.Join("；", problems);
        return picked.Count > 0 ? picked : null;
    }

    /// <summary>歌词片段 → 段下标。包含关系优先，其次相似度；曲库歌词错字多，要容错。</summary>
    /// <summary>把 order 切成项。引号内的内容永远是一整项。</summary>
    private static List<string> SplitOrderItems(string order, bool numericMode)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char ch in order ?? "")
        {
            if (ch == '"' || ch == '“' || ch == '”' || ch == '「' || ch == '」')
            {
                quoted = !quoted;
                continue;
            }
            bool isSeparator = !quoted &&
                (ch == ',' || ch == ';' || ch == '|' ||
                 (numericMode && (ch == '，' || ch == '、' || ch == ' ' ||
                                  ch == '；' || ch == '>')));
            if (isSeparator)
            {
                items.Add(current.ToString());
                current.Length = 0;
                continue;
            }
            current.Append(ch);
        }
        items.Add(current.ToString());
        return items;
    }

    /// <summary>整串只有数字和分隔符时按老写法(逗号分隔段号)处理。</summary>
    private static bool LooksLikeNumericOrder(string order)
    {
        foreach (char ch in order ?? "")
        {
            if (char.IsDigit(ch)) continue;
            if (ch == ',' || ch == '，' || ch == ' ' || ch == '、' ||
                ch == ';' || ch == '；' || ch == '>' || ch == '|') continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// 歌词片段有多少落在这一段里。**不用对称相似度**——短片段配长句子会被长度差
    /// 惩罚到过不了线（"你的眼泪像一颗虎珀" 对整句只得 0.39），而片段匹配天生长度悬殊。
    /// </summary>
    private static float LyricContainment(string fragment, string stored)
    {
        if (fragment.Length < 2 || stored.Length < 2) return 0f;
        var a = new HashSet<string>();
        for (int i = 0; i + 1 < fragment.Length; i++) a.Add(fragment.Substring(i, 2));
        var b = new HashSet<string>();
        for (int i = 0; i + 1 < stored.Length; i++) b.Add(stored.Substring(i, 2));
        if (a.Count == 0 || b.Count == 0) return 0f;
        int shared = 0;
        foreach (string g in a) if (b.Contains(g)) shared++;
        return shared / (float)Mathf.Min(a.Count, b.Count);
    }

    private List<int> MatchPracticePhrasesByLyric(string fragment)
    {
        var exact = new List<int>();
        var fuzzy = new List<int>();
        string probe = NormalizeLyricForTake(fragment);
        if (probe.Length == 0) return exact;
        for (int i = 0; i < m_PracticePhrases.Count; i++)
        {
            string stored = NormalizeLyricForTake(m_PracticePhrases[i].Lyrics);
            if (stored.Length == 0) continue;
            if (stored.Contains(probe) || probe.Contains(stored)) exact.Add(i);
            else if (LyricContainment(probe, stored) >= 0.60f) fuzzy.Add(i);
        }
        return exact.Count > 0 ? exact : fuzzy;
    }

    /// <summary>
    /// Starts a new in-memory practice sequence. Persistent song memories are untouched.
    /// </summary>
    public void BeginSingingPracticeSession()
    {
        m_PracticePhrases.Clear();
        m_QuarantinedSingingCandidates.Clear();
        m_RejectedQuarantineSignatures.Clear();
        m_RejectedQuarantineSessionSerials.Clear();
        m_LastCommittedPracticeSignature = 0;
        m_LastPracticeCommitTime = -999f;
        Debug.Log("[SenseVoice/Practice] 新练唱会话已开始；等待最终确认的歌唱片段");
    }

    /// <summary>
    /// Agent Loop 重启时调用：**不清空**，只丢掉真正陈旧的片段。
    ///
    /// 原来这里是无条件 Clear。而 Loop 停/起并不结束对话——m_DataList 一个字都不会掉，
    /// 她记的 note、memory 全都还在。8/25 实测：用户手动停了一次实时模式，
    /// 五段刚教的旋律当场蒸发，她的笔记却还写着「第5段是ウピラントア遥かな」，
    /// 于是照着笔记发 order="5"，两次都撞上"练唱会话里还没有任何片段"，
    /// 最后说出「私の記憶が壊れてしまったかしら？」。
    ///
    /// 这和 8/17 那次是同一个道理：清空是一个决定，不该是"重启了一下"的副作用。
    /// 段落自己带着"多久以前唱的"，感知帧也照实报出来——真正该丢的只有陈旧的那些。
    /// </summary>
    /// <param name="maxAgeSeconds">超过这个岁数的片段才丢。</param>
    public void ResumeSingingPracticeSession(
        float maxAgeSeconds, out int kept, out int dropped)
    {
        dropped = 0;
        // Delayed confirmations can append older recordings after newer ones.
        for (int i = m_PracticePhrases.Count - 1; i >= 0; i--)
        {
            if (Time.realtimeSinceStartup - m_PracticePhrases[i].AtRealtime <= maxAgeSeconds) continue;
            m_PracticePhrases.RemoveAt(i);
            dropped++;
        }
        int droppedCandidates = 0;
        for (int i = m_QuarantinedSingingCandidates.Count - 1; i >= 0; i--)
        {
            PracticePhrase phrase = m_QuarantinedSingingCandidates[i].Phrase;
            if (phrase != null &&
                Time.realtimeSinceStartup - phrase.AtRealtime <= maxAgeSeconds) continue;
            m_QuarantinedSingingCandidates.RemoveAt(i);
            droppedCandidates++;
        }
        kept = m_PracticePhrases.Count;
        //丢过东西就得让签名失效：段号已经整体前移，旧签名对应的不再是同一段。
        if (dropped > 0)
        {
            m_LastCommittedPracticeSignature = 0;
            m_LastPracticeCommitTime = -999f;
        }
        if (kept == 0 && dropped == 0)
            Debug.Log("[SenseVoice/Practice] 新练唱会话已开始；等待最终确认的歌唱片段");
        else
            Debug.Log($"[SenseVoice/Practice] 练唱会话已延续：留下 {kept} 段" +
                      (dropped > 0 ? $"，丢掉 {dropped} 段陈旧片段（超过 {maxAgeSeconds:F0} 秒）" : "") +
                      (droppedCandidates > 0
                          ? $"；另清理 {droppedCandidates} 个陈旧来源候选"
                          : $"；保留 {m_QuarantinedSingingCandidates.Count} 个来源待确认候选"));
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
        //
        //8/25：上面这段注释写了三个月，**代码里一个阈值都没有**——直接取第一条。
        //代价当场兑现：用户唱的日文段被标上 `疑似曲库 id=0947a4b23e25`，
        //而那条是 8/24 的一首中文歌「一瞬间紧紧拥抱，无处可逃…」，相似度 0.43。
        //练唱会话被削到只剩这一段之后，她照着这个 id 发了 <song_sing/>，
        //系统就把那首中文歌唱了出来——她是**照着帧做的**，错的是帧。
        string topRecallId = "";
        string topRecallName = "";
        if (m_LastSongRecall != null)
        {
            foreach (var item in m_LastSongRecall)
            {
                if (item == null || string.IsNullOrEmpty(item.song_id)) continue;
                //低于这条线的候选不是"弱线索"，是**负线索**：随便挑两首不相干的歌
                //都能得到比它更高的分。印出来只会把她往错的方向推。
                if (item.confidence < k_RecallIdentityFloor)
                {
                    Debug.Log($"[SenseVoice/Practice] 首选候选 {item.song_id} 相似 " +
                              $"{item.confidence:F2} < {k_RecallIdentityFloor:F2}(跨歌中位)，" +
                              "不作为身份线索写进练唱清单");
                    break;
                }
                topRecallId = item.song_id;
                topRecallName = item.named ? (item.display_name ?? "").Trim() : "";
                break;
            }
        }

        SingingEvidenceSnapshot cachedEvidence = m_LastSingingCacheEvidence;
        var phrase = new PracticePhrase
        {
            RecordingEvidence = CloneSingingEvidence(cachedEvidence),
            WavBytes = wavBytes,
            RecoveryWavBytes = m_LastSingingRecoveryAudioBytes != null
                ? (byte[])m_LastSingingRecoveryAudioBytes.Clone()
                : null,
            MidiTimeline = timeline,
            RecoveryMidiTimeline = HasPlayablePitchTimeline(
                    m_LastSingingRecoveryPerformanceMidi)
                ? (float[])m_LastSingingRecoveryPerformanceMidi.Clone()
                : null,
            FrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f),
            Language = language ?? "",
            Signature = signature,
            CaptureSessionSerial = cachedEvidence != null
                ? cachedEvidence.CaptureSessionSerial
                : m_LastSingingCacheCaptureSessionSerial,
            //分段歌词比整轮文本更贴近真正唱的那一段
            Lyrics = !string.IsNullOrWhiteSpace(m_LastSingingLyrics)
                ? m_LastSingingLyrics.Trim()
                : (LastText ?? "").Trim(),
            Seconds = GetWavDurationSeconds(wavBytes),
            RecoverySeconds = m_LastSingingRecoveryAudioBytes != null
                ? GetWavDurationSeconds(m_LastSingingRecoveryAudioBytes)
                : GetWavDurationSeconds(wavBytes),
            //本轮曲库回忆的首选就是现成的身份线索，不额外算
            SongId = topRecallId,
            SongName = topRecallName,
            AtRealtime = cachedEvidence != null
                ? cachedEvidence.AtRealtime
                : (m_LastAnalyzedCaptureAt >= 0f
                    ? m_LastAnalyzedCaptureAt : Time.realtimeSinceStartup),
            ConfirmedAtRealtime = Time.realtimeSinceStartup,
            PrecedingSpeech = m_LastSpokenTranscript ?? "",
            PendingConfirmation = pendingConfirmation,
            HeadExtraSeconds = cachedEvidence != null
                ? cachedEvidence.HeadExtraSeconds : m_LastHeadExtraSeconds,
            TailExtraSeconds = cachedEvidence != null
                ? cachedEvidence.TailExtraSeconds : m_LastTailExtraSeconds,
            HeadExtraText = cachedEvidence != null
                ? cachedEvidence.HeadExtraText : (m_LastHeadExtraText ?? ""),
            TailExtraText = cachedEvidence != null
                ? cachedEvidence.TailExtraText : (m_LastTailExtraText ?? ""),
            HeadExtraType = cachedEvidence != null
                ? cachedEvidence.HeadExtraType : (m_LastHeadExtraType ?? "none"),
            TailExtraType = cachedEvidence != null
                ? cachedEvidence.TailExtraType : (m_LastTailExtraType ?? "none"),
            HeadExtraProbability = cachedEvidence != null
                ? cachedEvidence.HeadExtraProbability : m_LastHeadExtraProbability,
            TailExtraProbability = cachedEvidence != null
                ? cachedEvidence.TailExtraProbability : m_LastTailExtraProbability,
            HeadExtraReviewRequired = cachedEvidence != null
                ? cachedEvidence.HeadExtraReviewRequired : m_LastHeadExtraReviewRequired,
            TailExtraReviewRequired = cachedEvidence != null
                ? cachedEvidence.TailExtraReviewRequired : m_LastTailExtraReviewRequired,
            HeadExtraMelodicSeconds = cachedEvidence != null
                ? cachedEvidence.HeadExtraMelodicSeconds : m_LastHeadExtraMelodicSeconds,
            TailExtraMelodicSeconds = cachedEvidence != null
                ? cachedEvidence.TailExtraMelodicSeconds : m_LastTailExtraMelodicSeconds,
            HeadExtraMelodicRatio = cachedEvidence != null
                ? cachedEvidence.HeadExtraMelodicRatio : m_LastHeadExtraMelodicRatio,
            TailExtraMelodicRatio = cachedEvidence != null
                ? cachedEvidence.TailExtraMelodicRatio : m_LastTailExtraMelodicRatio,
            HeadExtraLongestMelodicRunSeconds = cachedEvidence != null
                ? cachedEvidence.HeadExtraLongestMelodicRunSeconds
                : m_LastHeadExtraLongestMelodicRunSeconds,
            TailExtraLongestMelodicRunSeconds = cachedEvidence != null
                ? cachedEvidence.TailExtraLongestMelodicRunSeconds
                : m_LastTailExtraLongestMelodicRunSeconds,
            HeadExtraSegments = CloneBoundarySegments(cachedEvidence != null
                ? cachedEvidence.HeadExtraSegments : m_LastHeadExtraSegments),
            TailExtraSegments = CloneBoundarySegments(cachedEvidence != null
                ? cachedEvidence.TailExtraSegments : m_LastTailExtraSegments),
            CleanLeadInUnverifiedSeconds = cachedEvidence != null
                ? cachedEvidence.CleanLeadInUnverifiedSeconds
                : m_LastCleanLeadInUnverifiedSeconds,
            CleanLeadInEvidenceText = cachedEvidence != null
                ? cachedEvidence.CleanLeadInEvidenceText
                : (m_LastCleanLeadInEvidenceText ?? ""),
            CleanLeadInEvidenceType = cachedEvidence != null
                ? cachedEvidence.CleanLeadInEvidenceType
                : (m_LastCleanLeadInEvidenceType ?? "none"),
            CleanLeadInEvidenceProbability = cachedEvidence != null
                ? cachedEvidence.CleanLeadInEvidenceProbability
                : m_LastCleanLeadInEvidenceProbability,
        };
        var priorCandidate = m_QuarantinedSingingCandidates.Find(c =>
            (phrase.CaptureSessionSerial > 0 && c.Phrase.CaptureSessionSerial == phrase.CaptureSessionSerial) ||
            (phrase.AtRealtime > 0f && c.Phrase.AtRealtime == phrase.AtRealtime));
        if (priorCandidate != null)
        {
            phrase.ClipRef = priorCandidate.Phrase.ClipRef;
            phrase.RecordingSequence = EnsureRecordingSequence(priorCandidate.Phrase);
            RetainRecordingEvidence(phrase, priorCandidate.Phrase.LatestRecordingEvidence);
        }
        phraseCount = StorePracticeCapture(phrase);
        RemoveQuarantinedCandidateCapturedAt(phrase.AtRealtime);
        m_LastCommittedPracticeSignature = signature;
        m_LastPracticeCommitTime = Time.realtimeSinceStartup;
        float recoverySeconds = m_LastSingingRecoveryAudioBytes != null
            ? GetWavDurationSeconds(m_LastSingingRecoveryAudioBytes)
            : GetWavDurationSeconds(wavBytes);
        Debug.Log($"[SenseVoice/Practice] 最终歌声已提交 sequence={phraseCount} " +
                  $"audio={GetWavDurationSeconds(wavBytes):F2}s frames={timeline.Length}" +
                  (recoverySeconds > GetWavDurationSeconds(wavBytes) + 0.20f
                      ? $" expanded={recoverySeconds:F2}s"
                      : "") +
                  (pendingConfirmation ? " (待确认)" : ""));
        return true;
    }

    private int StorePracticeCapture(PracticePhrase phrase)
    {
        InitializePracticeSource(phrase);
        EnsureRecordingSequence(phrase);
        RetainRecordingEvidence(phrase, phrase.RecordingEvidence);
        RetainRecordingEvidence(phrase, m_LastSingingEvidence);
        // A resumed, unheard capture is the same recording with more samples,
        // not a second performance. Keep its identity if an early result was stored.
        for (int i = 0; i < m_PracticePhrases.Count; i++)
        {
            if (phrase.AtRealtime <= 0f || m_PracticePhrases[i].AtRealtime != phrase.AtRealtime) continue;
            phrase.StableId = m_PracticePhrases[i].StableId;
            phrase.ClipRef = m_PracticePhrases[i].ClipRef;
            phrase.RecordingSequence = EnsureRecordingSequence(m_PracticePhrases[i]);
            RetainRecordingEvidence(phrase, m_PracticePhrases[i].LatestRecordingEvidence);
            m_PracticePhrases[i] = phrase;
            Debug.Log($"[SenseVoice/Practice] 续音更新同一录音 practice={i + 1} stable_id={phrase.StableId}");
            return i + 1;
        }
        if (m_PracticePhrases.Count >= MaxPracticePhraseCount)
            m_PracticePhrases.RemoveAt(0);
        phrase.StableId = ++m_NextPracticePhraseStableId;
        m_PracticePhrases.Add(phrase);
        return m_PracticePhrases.Count;
    }

    private static void InitializePracticeSource(PracticePhrase phrase)
    {
        if (phrase == null) return;
        if (phrase.SourceCleanWavBytes == null && phrase.WavBytes != null)
            phrase.SourceCleanWavBytes = (byte[])phrase.WavBytes.Clone();
        if (phrase.SourceCleanMidiTimeline == null && phrase.MidiTimeline != null)
            phrase.SourceCleanMidiTimeline = (float[])phrase.MidiTimeline.Clone();
        if (phrase.SourceExpandedWavBytes == null && phrase.RecoveryWavBytes != null)
            phrase.SourceExpandedWavBytes = (byte[])phrase.RecoveryWavBytes.Clone();
        if (phrase.SourceExpandedMidiTimeline == null && phrase.RecoveryMidiTimeline != null)
            phrase.SourceExpandedMidiTimeline = (float[])phrase.RecoveryMidiTimeline.Clone();
        // No separate recovery buffer is needed when both measured windows are
        // exactly the same. Never alias merely because their durations match.
        var evidence = phrase.RecordingEvidence;
        if (evidence != null &&
            Mathf.Abs(evidence.CleanStartSeconds - evidence.RecoveryStartSeconds) < .00001f &&
            Mathf.Abs(evidence.CleanEndSeconds - evidence.RecoveryEndSeconds) < .00001f)
        {
            if (phrase.SourceExpandedWavBytes == null && phrase.SourceCleanWavBytes != null)
                phrase.SourceExpandedWavBytes = (byte[])phrase.SourceCleanWavBytes.Clone();
            if (phrase.SourceExpandedMidiTimeline == null && phrase.SourceCleanMidiTimeline != null)
                phrase.SourceExpandedMidiTimeline = (float[])phrase.SourceCleanMidiTimeline.Clone();
        }
        if (phrase.OriginalCleanSeconds <= 0f)
            phrase.OriginalCleanSeconds = GetWavDurationSeconds(
                phrase.SourceCleanWavBytes ?? phrase.WavBytes);
        if (phrase.OriginalExpandedSeconds <= 0f)
            phrase.OriginalExpandedSeconds = GetWavDurationSeconds(
                phrase.SourceExpandedWavBytes ?? phrase.RecoveryWavBytes ??
                phrase.SourceCleanWavBytes ?? phrase.WavBytes);
        if (string.IsNullOrWhiteSpace(phrase.ActiveCapture)) phrase.ActiveCapture = "clean";
    }

    /// <summary>
    /// 暂存一份“可能是用户歌声，也可能是外放/背景音乐”的真实音频证据。
    /// 候选不会自动进入 practice 清单；角色可选择已有音频，来源判断独立保留。
    /// </summary>
    private bool m_CurrentRecordingEvidencePublished;

    /// <summary>
    /// Publish unrepresented recording evidence before the role sees the final turn.
    /// Playback eligibility and an early speech verdict must not erase its identity.
    /// Never use the recent playable cache here: it may belong to an older recording.
    /// </summary>
    public bool RetainCurrentSingingEvidenceClip(bool semanticSingingEvidence)
    {
        var evidence = m_LastSingingEvidence;
        UpdateRetainedRecordingEvidence(evidence);
        if (m_CurrentRecordingEvidencePublished || evidence == null || !HasUsableWavPayload(evidence.RawWavBytes) ||
            evidence.CaptureSessionSerial != m_LiveRecordingCandidateSessionSerial)
            return false;
        // This is evidence retention, not a singing/source verdict. Keep existing
        // uncertain-band policy; semantic evidence can retain lower acoustic scores.
        if (!semanticSingingEvidence && evidence.SingingProbability < 0.52f)
            return false;
        // Follow-up text/ticks cannot resurrect an explicitly dropped clip from
        // the still-current raw snapshot. Only a new recording resets publication.
        m_CurrentRecordingEvidencePublished = true;
        bool SameCapture(PracticePhrase p) => p != null &&
            (evidence.CaptureSessionSerial > 0
                ? p.CaptureSessionSerial == evidence.CaptureSessionSerial
                : Mathf.Abs(p.AtRealtime - evidence.AtRealtime) < .001f);
        if (m_PracticePhrases.Any(SameCapture) ||
            m_QuarantinedSingingCandidates.Any(c => SameCapture(c.Phrase)))
            return false;

        // No automatic clean/expanded choice, provenance confirmation or playback.
        // The snapshot supplies ALL audio and pitch for subsequent explicit selection.
        bool retained = QuarantineSingingEvidence(evidence, null,
            evidence.PitchTimelineMidi, evidence.FrameSeconds, evidence.Language,
            false, false, null, null, "", out int candidateId, out _);
        if (retained)
        {
            var candidate = m_QuarantinedSingingCandidates.Find(c => c.CandidateId == candidateId);
            Debug.Log($"[SenseVoice/Clip] retained={candidate.Phrase.ClipRef} " +
                $"session={evidence.CaptureSessionSerial} raw={evidence.RawSeconds:F2}s " +
                $"clean={evidence.CleanEndSeconds - evidence.CleanStartSeconds:F2}s " +
                $"expanded={evidence.RecoveryEndSeconds - evidence.RecoveryStartSeconds:F2}s " +
                $"source=pending playback={candidate.PlaybackStatus} played=false；" +
                "可引用证据独立于自动播放门槛，是否歌唱与范围交给角色判断");
        }
        return retained;
    }

    public bool QuarantineRecentSingingCandidate(
        out int candidateId, out float seconds)
    {
        candidateId = 0;
        seconds = 0f;
        byte[] wavBytes = null;
        float[] timeline = null;
        float frameSeconds = 0.10f;
        string language = "";
        bool ownsCurrentPlaybackAlias = HasCurrentSingingPerformanceCandidate();
        bool playbackReady = ownsCurrentPlaybackAlias &&
            TryGetRecentSingingAudio(out wavBytes) &&
            TryGetRecentSingingPerformance(out timeline, out frameSeconds,
                out language);
        SingingEvidenceSnapshot evidence = playbackReady &&
            m_LastSingingCacheEvidence != null
                ? m_LastSingingCacheEvidence : m_LastSingingEvidence;
        if (!playbackReady && (evidence == null || evidence.RawWavBytes == null ||
            evidence.RawWavBytes.Length <= 44))
        {
            if (ownsCurrentPlaybackAlias)
                RevokeCurrentSingingPlaybackAlias(
                    "隔离取证不完整；宁可不可播放也不泄漏待确认素材");
            return false;
        }

        if (!playbackReady)
        {
            wavBytes = null;
            timeline = evidence.PitchTimelineMidi == null
                ? new float[0] : (float[])evidence.PitchTimelineMidi.Clone();
            frameSeconds = evidence.FrameSeconds;
            language = evidence.Language ?? "";
        }

        return QuarantineSingingEvidence(evidence, wavBytes, timeline, frameSeconds,
            language, playbackReady, ownsCurrentPlaybackAlias,
            m_LastSingingRecoveryAudioBytes, m_LastSingingRecoveryPerformanceMidi,
            m_LastSpokenTranscript, out candidateId, out seconds);
    }

    private bool QuarantineSingingEvidence(SingingEvidenceSnapshot evidence,
        byte[] wavBytes, float[] timeline, float frameSeconds, string language,
        bool playbackReady, bool ownsCurrentPlaybackAlias, byte[] recoveryWav,
        float[] recoveryTimeline, string precedingSpeech,
        out int candidateId, out float seconds)
    {
        candidateId = 0;
        seconds = 0f;

        byte[] signatureAudio = evidence != null &&
            evidence.RawWavBytes != null && evidence.RawWavBytes.Length > 44
                ? evidence.RawWavBytes : wavBytes;
        //来源身份锚定原始 WAV，而不是一次分析产出的 timeline 长度。迟到复核可能
        //让同一录音的音高帧数略变；若把帧数混进签名，用户刚否认的录音会换个签名
        //再次变成 pending。
        int signature = ComputePracticeSignature(signatureAudio, null);
        int captureSessionSerial = evidence != null
            ? evidence.CaptureSessionSerial : m_LastSingingCacheCaptureSessionSerial;
        bool rejectedRecording = captureSessionSerial > 0
            ? m_RejectedQuarantineSessionSerials.Contains(captureSessionSerial)
            : m_RejectedQuarantineSignatures.Contains(signature);
        if (rejectedRecording)
        {
            if (ownsCurrentPlaybackAlias)
                RevokeCurrentSingingPlaybackAlias(
                    "同一录音的来源已被用户否认，禁止再次发布");
            Debug.Log($"[SenseVoice/Quarantine] 同一录音 session={captureSessionSerial} " +
                      $"signature={signature} 已被用户否认；" +
                      "忽略迟到/重复分析，不重新创建 pending");
            return false;
        }
        UpdateRetainedRecordingEvidence(evidence);
        if (captureSessionSerial > 0 && m_PracticePhrases.Any(
            p => p.CaptureSessionSerial == captureSessionSerial))
            return false; // Retain new evidence without replacing the current playable version.
        for (int i = 0; i < m_QuarantinedSingingCandidates.Count; i++)
        {
            QuarantinedSingingCandidate existing = m_QuarantinedSingingCandidates[i];
            if (existing.Phrase == null) continue;
            bool sameRecording = captureSessionSerial > 0 &&
                existing.Phrase.CaptureSessionSerial == captureSessionSerial;
            if (!sameRecording && (captureSessionSerial > 0 || existing.Phrase.Signature != signature)) continue;
            candidateId = existing.CandidateId;
            seconds = existing.Phrase.Seconds;
            if (ownsCurrentPlaybackAlias)
                RevokeCurrentSingingPlaybackAlias(
                    $"候选 {candidateId} 已在隔离区，禁止经 recent_turn 播放");
            return true;
        }
        if (captureSessionSerial <= 0 && signature == m_LastCommittedPracticeSignature &&
            Time.realtimeSinceStartup - m_LastPracticeCommitTime < 8f)
        {
            if (ownsCurrentPlaybackAlias)
                RevokeCurrentSingingPlaybackAlias(
                    "本轮证据与刚提交素材重复，不保留临时 recent_turn 别名");
            return false;
        }

        var phrase = new PracticePhrase
        {
            RecordingEvidence = CloneSingingEvidence(evidence),
            WavBytes = wavBytes,
            RecoveryWavBytes = playbackReady && recoveryWav != null
                ? (byte[])recoveryWav.Clone()
                : null,
            MidiTimeline = timeline,
            RecoveryMidiTimeline = playbackReady && HasPlayablePitchTimeline(
                    recoveryTimeline)
                ? (float[])recoveryTimeline.Clone()
                : null,
            FrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f),
            Language = language ?? "",
            Signature = signature,
            CaptureSessionSerial = captureSessionSerial,
            //歌唱岛没有独立转写时保持未知；不能把含前后口语的整轮 ASR 冒充歌词。
            Lyrics = evidence != null ? (evidence.SingingText ?? "").Trim()
                : playbackReady && !string.IsNullOrWhiteSpace(m_LastResponseSingingText)
                ? m_LastResponseSingingText.Trim()
                : (evidence?.SingingText ?? "").Trim(),
            Seconds = playbackReady
                ? GetWavDurationSeconds(wavBytes) : evidence.RawSeconds,
            RecoverySeconds = playbackReady && recoveryWav != null
                ? GetWavDurationSeconds(recoveryWav)
                : (playbackReady ? GetWavDurationSeconds(wavBytes) : evidence.RawSeconds),
            //来源尚未确认时，曲库相似候选也不能被误当作身份事实。
            SongId = "",
            SongName = "",
            AtRealtime = evidence != null
                ? evidence.AtRealtime
                : (m_LastAnalyzedCaptureAt >= 0f
                    ? m_LastAnalyzedCaptureAt : Time.realtimeSinceStartup),
            ConfirmedAtRealtime = -1f,
            PrecedingSpeech = precedingSpeech ?? "",
            PendingConfirmation = true,
            HeadExtraSeconds = evidence != null
                ? evidence.HeadExtraSeconds : m_LastHeadExtraSeconds,
            TailExtraSeconds = evidence != null
                ? evidence.TailExtraSeconds : m_LastTailExtraSeconds,
            HeadExtraText = evidence != null
                ? evidence.HeadExtraText : (m_LastHeadExtraText ?? ""),
            TailExtraText = evidence != null
                ? evidence.TailExtraText : (m_LastTailExtraText ?? ""),
            HeadExtraType = evidence != null
                ? evidence.HeadExtraType : (m_LastHeadExtraType ?? "none"),
            TailExtraType = evidence != null
                ? evidence.TailExtraType : (m_LastTailExtraType ?? "none"),
            HeadExtraProbability = evidence != null
                ? evidence.HeadExtraProbability : m_LastHeadExtraProbability,
            TailExtraProbability = evidence != null
                ? evidence.TailExtraProbability : m_LastTailExtraProbability,
            HeadExtraReviewRequired = evidence != null
                ? evidence.HeadExtraReviewRequired : m_LastHeadExtraReviewRequired,
            TailExtraReviewRequired = evidence != null
                ? evidence.TailExtraReviewRequired : m_LastTailExtraReviewRequired,
            HeadExtraMelodicSeconds = evidence != null
                ? evidence.HeadExtraMelodicSeconds : m_LastHeadExtraMelodicSeconds,
            TailExtraMelodicSeconds = evidence != null
                ? evidence.TailExtraMelodicSeconds : m_LastTailExtraMelodicSeconds,
            HeadExtraMelodicRatio = evidence != null
                ? evidence.HeadExtraMelodicRatio : m_LastHeadExtraMelodicRatio,
            TailExtraMelodicRatio = evidence != null
                ? evidence.TailExtraMelodicRatio : m_LastTailExtraMelodicRatio,
            HeadExtraLongestMelodicRunSeconds = evidence != null
                ? evidence.HeadExtraLongestMelodicRunSeconds
                : m_LastHeadExtraLongestMelodicRunSeconds,
            TailExtraLongestMelodicRunSeconds = evidence != null
                ? evidence.TailExtraLongestMelodicRunSeconds
                : m_LastTailExtraLongestMelodicRunSeconds,
            HeadExtraSegments = CloneBoundarySegments(evidence != null
                ? evidence.HeadExtraSegments : m_LastHeadExtraSegments),
            TailExtraSegments = CloneBoundarySegments(evidence != null
                ? evidence.TailExtraSegments : m_LastTailExtraSegments),
            CleanLeadInUnverifiedSeconds = evidence != null
                ? evidence.CleanLeadInUnverifiedSeconds
                : m_LastCleanLeadInUnverifiedSeconds,
            CleanLeadInEvidenceText = evidence != null
                ? evidence.CleanLeadInEvidenceText
                : (m_LastCleanLeadInEvidenceText ?? ""),
            CleanLeadInEvidenceType = evidence != null
                ? evidence.CleanLeadInEvidenceType
                : (m_LastCleanLeadInEvidenceType ?? "none"),
            CleanLeadInEvidenceProbability = evidence != null
                ? evidence.CleanLeadInEvidenceProbability
                : m_LastCleanLeadInEvidenceProbability,
        };
        EnsureRecordingSequence(phrase);
        RetainRecordingEvidence(phrase, phrase.RecordingEvidence);
        RetainRecordingEvidence(phrase, m_LastSingingEvidence);
        candidateId = ++m_NextQuarantinedSingingCandidateId;
        phrase.OriginCandidateId = candidateId;
        if (m_QuarantinedSingingCandidates.Count >= MaxPracticePhraseCount)
        {
            QuarantinedSingingCandidate oldest = m_QuarantinedSingingCandidates[0];
            for (int i = 1; i < m_QuarantinedSingingCandidates.Count; i++)
            {
                if (m_QuarantinedSingingCandidates[i].Phrase.AtRealtime < oldest.Phrase.AtRealtime)
                    oldest = m_QuarantinedSingingCandidates[i];
            }
            m_QuarantinedSingingCandidates.Remove(oldest);
            Debug.LogWarning($"[SenseVoice/Quarantine] 会话候选达到 {MaxPracticePhraseCount} 个；" +
                             $"为限制内存仅清理最老 candidate={oldest.CandidateId}");
        }
        m_QuarantinedSingingCandidates.Add(new QuarantinedSingingCandidate
        {
            Evidence = phrase.RecordingEvidence,
            CandidateId = candidateId,
            Phrase = phrase,
            SourceConfirmed = false,
            PlaybackStatus = playbackReady
                ? "ready"
                : (timeline != null && timeline.Length > 0
                    ? "evidence_only" : "unavailable"),
            WholeTurnText = (evidence?.Text ?? LastText ?? "").Trim(),
            SingingSegmentText = (evidence?.SingingText ?? "").Trim(),
            RawWavBytes = evidence != null && evidence.RawWavBytes != null
                ? (byte[])evidence.RawWavBytes.Clone()
                : (playbackReady ? (byte[])wavBytes.Clone() : null),
            SingingProbability = evidence != null
                ? evidence.SingingProbability : LastSingingProbability,
            PitchStability = evidence != null
                ? evidence.PitchStability : LastPitchStability,
            MelodicIslandSeconds = evidence != null
                ? evidence.MelodicIslandSeconds : LastSingingIslandSeconds,
            ContentSeconds = evidence != null
                ? evidence.ContentSeconds : LastSingingContentSeconds,
        });
        seconds = phrase.Seconds;
        string playbackStatus = playbackReady
            ? "ready" : (timeline != null && timeline.Length > 0
                ? "evidence_only" : "unavailable");
        Debug.Log($"[SenseVoice/Quarantine] 来源不确定证据已隔离 candidate={candidateId} " +
                  $"audio={seconds:F2}s frames={(timeline == null ? 0 : timeline.Length)} " +
                  $"source=pending playback={playbackStatus}；会话现有 " +
                  $"{m_QuarantinedSingingCandidates.Count} 个待确认候选，均未进入练唱清单");
        if (ownsCurrentPlaybackAlias)
            RevokeCurrentSingingPlaybackAlias(
                $"candidate={candidateId} 来源待确认，只允许隔离区持有");
        return true;
    }

    // A detached final response must not touch LastText, current pitch, speaker,
    // recent_turn, or the active recording's evidence. Build an independent item.
    private void ArchiveCompletedCapture(Response response, byte[] raw, float capturedAt, int session)
    {
        if (response == null || !HasUsableWavPayload(raw)) return;
        float duration = GetWavDurationSeconds(raw);
        float frame = Mathf.Clamp(response.pitch_timeline_frame_seconds, .02f, .25f);
        float origin = response.audio_content_start_seconds + response.pitch_timeline_start_seconds;
        float start = Mathf.Clamp(response.audio_content_start_seconds + response.singing_start_seconds, 0f, duration);
        float end = response.singing_end_seconds > 0f
            ? Mathf.Clamp(response.audio_content_start_seconds + response.singing_end_seconds, start, duration)
            : duration;
        float recoveryStart = Mathf.Clamp(response.audio_content_start_seconds + response.singing_recovery_start_seconds, 0f, start);
        float recoveryEnd = response.singing_recovery_end_seconds > 0f
            ? Mathf.Clamp(response.audio_content_start_seconds + response.singing_recovery_end_seconds, end, duration)
            : end;
        var evidence = new SingingEvidenceSnapshot
        {
            CaptureSessionSerial = session, RawWavBytes = raw,
            TimelineOriginSeconds = origin,
            PitchTimelineMidi = response.pitch_timeline_midi ?? new float[0],
            FrameSeconds = frame, AtRealtime = capturedAt,
            Text = response.text ?? "", SingingText = response.singing_text ?? "",
            Language = string.IsNullOrWhiteSpace(response.singing_language) ? response.language : response.singing_language,
            RawSeconds = duration, CleanStartSeconds = start, CleanEndSeconds = end,
            RecoveryStartSeconds = recoveryStart, RecoveryEndSeconds = recoveryEnd,
            SingingProbability = response.singing_probability, PitchStability = response.pitch_stability,
            MelodicIslandSeconds = end - start, ContentSeconds = duration - response.audio_content_start_seconds,
            HeadExtraSeconds = Mathf.Max(0f, response.singing_head_extra_end_seconds - response.singing_head_extra_start_seconds),
            TailExtraSeconds = Mathf.Max(0f, response.singing_tail_extra_end_seconds - response.singing_tail_extra_start_seconds),
            HeadExtraText = response.singing_head_extra_text, TailExtraText = response.singing_tail_extra_text,
            HeadExtraType = response.singing_head_extra_type, TailExtraType = response.singing_tail_extra_type,
            HeadExtraProbability = response.singing_head_extra_probability, TailExtraProbability = response.singing_tail_extra_probability,
            HeadExtraReviewRequired = response.singing_head_extra_review_required, TailExtraReviewRequired = response.singing_tail_extra_review_required,
            HeadExtraMelodicSeconds = response.singing_head_extra_melodic_seconds, TailExtraMelodicSeconds = response.singing_tail_extra_melodic_seconds,
            HeadExtraMelodicRatio = response.singing_head_extra_melodic_ratio, TailExtraMelodicRatio = response.singing_tail_extra_melodic_ratio,
            HeadExtraLongestMelodicRunSeconds = response.singing_head_extra_longest_melodic_run_seconds,
            TailExtraLongestMelodicRunSeconds = response.singing_tail_extra_longest_melodic_run_seconds,
            HeadExtraSegments = CloneBoundarySegments(response.singing_head_extra_segments),
            TailExtraSegments = CloneBoundarySegments(response.singing_tail_extra_segments),
        };
        float[] timeline = SliceFloatArray(evidence.PitchTimelineMidi,
            Mathf.FloorToInt(Mathf.Max(0f, start - origin) / frame),
            Mathf.CeilToInt(Mathf.Max(0f, end - origin) / frame));
        float[] recoveryTimeline = SliceFloatArray(evidence.PitchTimelineMidi,
            Mathf.FloorToInt(Mathf.Max(0f, recoveryStart - origin) / frame),
            Mathf.CeilToInt(Mathf.Max(0f, recoveryEnd - origin) / frame));
        byte[] clean = TrimWavWindow(raw, start, end, out _, out _);
        byte[] expanded = TrimWavWindow(raw, recoveryStart, recoveryEnd, out _, out _);
        bool ready = response.singing_analysis_available && start >= origin - .05f &&
            end - start >= k_MinSingablePerformanceSeconds &&
            HasPlayablePitchTimeline(timeline) && HasUsableWavPayload(clean);
        QuarantineSingingEvidence(evidence, ready ? clean : null, timeline, frame,
            evidence.Language, ready, false, expanded, recoveryTimeline, "",
            out int candidateId, out float seconds);
        Debug.Log($"[ASR/Archive] 已录完音频迟到归档 session={session} candidate={candidateId} " +
            $"raw={duration:F2}s clean={end - start:F2}s；来源/是否歌唱待角色判断，不覆盖新轮感知，不启动旧回复");
    }

    /// <summary>按真实录音顺序返回本练唱会话的全部来源待确认候选。</summary>
    public List<QuarantinedSingingCandidateInfo> DescribeQuarantinedSingingCandidates()
    {
        var list = new List<QuarantinedSingingCandidateInfo>(
            m_QuarantinedSingingCandidates.Count);
        for (int i = 0; i < m_QuarantinedSingingCandidates.Count; i++)
        {
            QuarantinedSingingCandidate item = m_QuarantinedSingingCandidates[i];
            if (item == null || item.Phrase == null) continue;
            list.Add(new QuarantinedSingingCandidateInfo
            {
                ClipRef = item.Phrase.ClipRef,
                RecordingSequence = EnsureRecordingSequence(item.Phrase),
                CurrentCapture = item.PlaybackStatus == "ready" ? item.Phrase.ActiveCapture : "unselected",
                CurrentStartSeconds = (item.Phrase.ActiveCapture == "expanded"
                    ? item.Evidence?.RecoveryStartSeconds ?? 0f : item.Evidence?.CleanStartSeconds ?? 0f) + item.Phrase.ActiveTrimHeadSeconds,
                CurrentEndSeconds = (item.Phrase.ActiveCapture == "expanded"
                    ? item.Evidence?.RecoveryEndSeconds ?? 0f : item.Evidence?.CleanEndSeconds ?? 0f) - item.Phrase.ActiveTrimTailSeconds,
                RawSeconds = item.Evidence?.RawSeconds ?? 0f,
                CleanStartSeconds = item.Evidence?.CleanStartSeconds ?? 0f,
                CleanEndSeconds = item.Evidence?.CleanEndSeconds ?? 0f,
                ExpandedStartSeconds = item.Evidence?.RecoveryStartSeconds ?? 0f,
                ExpandedEndSeconds = item.Evidence?.RecoveryEndSeconds ?? 0f,
                CandidateId = item.CandidateId,
                HasRecoveryEvidence = item.Evidence != null,
                CleanSeconds = item.Evidence == null ? 0f : item.Evidence.CleanEndSeconds - item.Evidence.CleanStartSeconds,
                ExpandedSeconds = item.Evidence == null ? 0f : item.Evidence.RecoveryEndSeconds - item.Evidence.RecoveryStartSeconds,
                Lyrics = item.Phrase.Lyrics ?? "",
                PrecedingSpeech = item.Phrase.PrecedingSpeech ?? "",
                WholeTurnText = item.WholeTurnText ?? "",
                SingingSegmentText = item.SingingSegmentText ?? "",
                Seconds = item.Phrase.Seconds,
                AgoSeconds = Mathf.Max(
                    0f, Time.realtimeSinceStartup - item.Phrase.AtRealtime),
                SourceStatus = item.SourceConfirmed ? "confirmed_user" : "pending",
                PlaybackStatus = string.IsNullOrWhiteSpace(item.PlaybackStatus)
                    ? "unavailable" : item.PlaybackStatus,
                SingingProbability = item.SingingProbability,
                PitchStability = item.PitchStability,
                MelodicIslandSeconds = item.MelodicIslandSeconds,
                ContentSeconds = item.ContentSeconds,
                HeadExtraSeconds = item.Phrase.HeadExtraSeconds,
                TailExtraSeconds = item.Phrase.TailExtraSeconds,
                HeadExtraText = item.Phrase.HeadExtraText ?? "",
                TailExtraText = item.Phrase.TailExtraText ?? "",
                HeadExtraType = item.Phrase.HeadExtraType ?? "none",
                TailExtraType = item.Phrase.TailExtraType ?? "none",
                HeadExtraReviewRequired = item.Phrase.HeadExtraReviewRequired,
                TailExtraReviewRequired = item.Phrase.TailExtraReviewRequired,
                HeadExtraMelodicSeconds = item.Phrase.HeadExtraMelodicSeconds,
                TailExtraMelodicSeconds = item.Phrase.TailExtraMelodicSeconds,
                HeadExtraMelodicRatio = item.Phrase.HeadExtraMelodicRatio,
                TailExtraMelodicRatio = item.Phrase.TailExtraMelodicRatio,
                HeadExtraLongestMelodicRunSeconds =
                    item.Phrase.HeadExtraLongestMelodicRunSeconds,
                TailExtraLongestMelodicRunSeconds =
                    item.Phrase.TailExtraLongestMelodicRunSeconds,
                HeadExtraSegments = CloneBoundarySegments(item.Phrase.HeadExtraSegments),
                TailExtraSegments = CloneBoundarySegments(item.Phrase.TailExtraSegments),
                CleanLeadInUnverifiedSeconds =
                    item.Phrase.CleanLeadInUnverifiedSeconds,
                CleanLeadInEvidenceText =
                    item.Phrase.CleanLeadInEvidenceText ?? "",
                CleanLeadInEvidenceType =
                    item.Phrase.CleanLeadInEvidenceType ?? "none",
                CleanLeadInEvidenceProbability =
                    item.Phrase.CleanLeadInEvidenceProbability,
            });
        }
        list.Sort((a, b) =>
        {
            int time = b.AgoSeconds.CompareTo(a.AgoSeconds);
            return time != 0 ? time : a.CandidateId.CompareTo(b.CandidateId);
        });
        list.Sort((a, b) => a.RecordingSequence.CompareTo(b.RecordingSequence));
        for (int i = 0; i < list.Count; i++) list[i].CaptureOrder = list[i].RecordingSequence;
        return list;
    }

    /// <summary>兼容旧调用：返回最近录到的一个候选；完整列表请用 Describe。</summary>
    public bool TryGetQuarantinedSingingCandidateFacts(
        out int candidateId,
        out string lyrics,
        out string precedingSpeech,
        out float seconds)
    {
        candidateId = 0;
        lyrics = "";
        precedingSpeech = "";
        seconds = 0f;
        QuarantinedSingingCandidate latest = null;
        for (int i = 0; i < m_QuarantinedSingingCandidates.Count; i++)
        {
            QuarantinedSingingCandidate item = m_QuarantinedSingingCandidates[i];
            if (item == null || item.Phrase == null) continue;
            if (latest == null || item.Phrase.AtRealtime > latest.Phrase.AtRealtime)
                latest = item;
        }
        if (latest == null) return false;
        candidateId = latest.CandidateId;
        lyrics = latest.Phrase.Lyrics ?? "";
        precedingSpeech = latest.Phrase.PrecedingSpeech ?? "";
        seconds = latest.Phrase.Seconds;
        return true;
    }

    /// <summary>按固定 clip 身份解析当前存储位置；不选择范围，不推断来源。</summary>
    public bool TryResolveSingingClip(string clipRef, out int stableId, out int candidateId)
    {
        stableId = 0;
        candidateId = 0;
        if (string.IsNullOrWhiteSpace(clipRef)) return false;
        clipRef = clipRef.Trim();
        var phrase = m_PracticePhrases.Find(p => p.ClipRef == clipRef);
        if (phrase != null) { stableId = phrase.StableId; return true; }
        var candidate = m_QuarantinedSingingCandidates.Find(c => c.Phrase.ClipRef == clipRef);
        if (candidate == null) return false;
        candidateId = candidate.CandidateId;
        return true;
    }

    // Called only after action authorization. Source assertions come from the
    // model, not keywords in user text. A failed request never substitutes audio.
    public bool TryPrepareSingingClip(string clipRef, bool confirmUser, string range,
        out int stableId, out string failure)
    {
        failure = "";
        if (!TryResolveSingingClip(clipRef, out stableId, out int candidateId))
        { failure = $"{clipRef} 不存在或已移除；未选择其它素材。"; return false; }
        range = string.IsNullOrWhiteSpace(range) ? "current" : range.Trim().ToLowerInvariant();
        if (range != "current" && range != "clean" && range != "expanded")
        { failure = "range 只能为 current、clean、expanded。"; return false; }
        if (candidateId > 0)
        {
            var candidate = m_QuarantinedSingingCandidates.Find(c => c.CandidateId == candidateId);
            if (candidate.PlaybackStatus != "ready" && range == "current")
            { failure = $"{clipRef} 原始录音仍在，但当前没有可播放版本。请根据边界证据选择 range=\"clean\" 或 \"expanded\"，也可询问；来源确认不是播放成功。"; return false; }
            if (range != "current" && !PrepareQuarantinedCaptureForConfirmation(
                    candidateId, range, 0f, 0f, out failure)) return false;
            if (!AdmitSelectedSingingCandidate(candidateId, confirmUser, out _, out _) ||
                !TryResolveSingingClip(clipRef, out stableId, out _) || stableId <= 0)
            { failure = $"{clipRef} 所选音频仍不可播放；未选择其它素材。"; return false; }
        }
        else if (range != "current")
        {
            if (!TryRevisePracticePhrase(stableId, range, 0f, 0f, false, out _, out failure, 1f))
                return false;
        }
        if (confirmUser) ConfirmSingingClipSource(clipRef, out _);
        return true;
    }

    public bool TrySelectSingingClipWindow(string clipRef, bool confirmUser,
        float startSeconds, float endSeconds, bool excludeSpeech, out int stableId, out string failure)
    {
        failure = "";
        if (!TryResolveSingingClip(clipRef, out stableId, out int candidateId))
        { failure = "指定 clip 不存在。"; return false; }
        var record = m_QuarantinedSingingCandidates.Find(c => c.CandidateId == candidateId);
        int selectedStableId = stableId;
        var phrase = record != null ? record.Phrase : m_PracticePhrases.Find(p => p.StableId == selectedStableId);
        var evidence = phrase?.LatestRecordingEvidence ?? phrase?.RecordingEvidence;
        if (evidence == null || float.IsNaN(startSeconds) || float.IsNaN(endSeconds) ||
            float.IsInfinity(startSeconds) || float.IsInfinity(endSeconds) ||
            startSeconds < evidence.RecoveryStartSeconds || endSeconds > evidence.RecoveryEndSeconds ||
            endSeconds - startSeconds < 1f)
        { failure = "原录音坐标范围无效、少于1秒或超出可恢复音频范围；原始录音与当前版本未改动。"; return false; }
        float head = startSeconds - evidence.RecoveryStartSeconds;
        float tail = evidence.RecoveryEndSeconds - endSeconds;
        if (record != null)
        {
            if (!PrepareQuarantinedCaptureForConfirmation(candidateId, "expanded", head, tail, out failure)) return false;
            if (!AdmitSelectedSingingCandidate(candidateId, confirmUser, out _, out _) ||
                !TryResolveSingingClip(clipRef, out stableId, out _) || stableId <= 0)
            { failure = "范围准备后仍不可播放。"; return false; }
            // The regular execution validator still checks speech-boundary conflicts.
            return true;
        }
        bool revised = TryRevisePracticePhrase(stableId, "expanded", head, tail, excludeSpeech,
            out _, out failure, 1f);
        if (revised && confirmUser) ConfirmSingingClipSource(clipRef, out _);
        return revised;
    }

    public bool PrepareQuarantinedCaptureForConfirmation(int candidateId, string capture,
        float trimHead, float trimTail, out string error)
    {
        int index = FindQuarantinedCandidateIndex(candidateId);
        if (index < 0) { error = "候选不存在；没有改用同号 stable 或旧缓存。"; return false; }
        var record = m_QuarantinedSingingCandidates[index];
        var evidence = record.Phrase.LatestRecordingEvidence ?? record.Evidence;
        if (!TryPrepareEvidenceRange(record.Phrase, evidence, capture, trimHead, trimTail,
                out var prepared, out error)) return false;
        record.Phrase = prepared;
        record.Evidence = prepared.RecordingEvidence;
        record.PlaybackStatus = "ready";
        // Preparing or playing audio is not a provenance decision.
        Debug.Log($"[SenseVoice/Recovery] candidate={candidateId} selected={capture} " +
            $"audio={prepared.Seconds:F2}s；角色选定范围，尚未播放，来源状态未改变");
        return true;
    }

    public bool ConfirmQuarantinedSingingCandidate(
        int candidateId, out int phraseIndex)
    {
        return ConfirmQuarantinedSingingCandidateWithPlaybackStatus(
            candidateId, out phraseIndex, out string _);
    }

    /// <summary>
    /// 来源确认和播放资格是两条状态轴。确认不足 3 秒或边界仍不可靠的原始证据时，
    /// 保留 candidate 并标为 confirmed_user；绝不为了“确认成功”把它硬塞进可播放清单。
    /// </summary>
    public bool ConfirmQuarantinedSingingCandidateWithPlaybackStatus(
        int candidateId, out int phraseIndex, out string playbackStatus)
    {
        return AdmitSelectedSingingCandidate(candidateId, true, out phraseIndex, out playbackStatus);
    }

    // Explicit audio selection and source attribution are independent decisions.
    public bool ConfirmSingingClipSource(string clipRef, out string playbackStatus)
    {
        playbackStatus = "unavailable";
        if (!TryResolveSingingClip(clipRef, out int stableId, out int candidateId)) return false;
        if (candidateId > 0)
            return AdmitSelectedSingingCandidate(candidateId, true, out _, out playbackStatus);
        var phrase = m_PracticePhrases.Find(p => p.StableId == stableId);
        if (phrase == null) return false;
        if (phrase.PendingConfirmation) phrase.ConfirmedAtRealtime = Time.realtimeSinceStartup;
        phrase.PendingConfirmation = false;
        playbackStatus = "ready";
        return true;
    }

    private bool AdmitSelectedSingingCandidate(int candidateId, bool confirmSource,
        out int phraseIndex, out string playbackStatus)
    {
        phraseIndex = 0;
        playbackStatus = "unavailable";
        //ready 候选确认后会离开 quarantine、进入 practice。再次确认同一来源是无害
        //重试，不应被当作失败并连带阻止同轮 hum_back；稳定映射仍是唯一真相。
        if (TryResolveAdmittedSingingCandidate(
                candidateId, out int stableId, out phraseIndex))
        {
            var existing = m_PracticePhrases.Find(p => p.StableId == stableId);
            if (confirmSource && existing.PendingConfirmation)
            {
                existing.PendingConfirmation = false;
                existing.ConfirmedAtRealtime = Time.realtimeSinceStartup;
            }
            playbackStatus = "ready";
            Debug.Log($"[SenseVoice/Quarantine] candidate={candidateId} 已有可播放映射；" +
                      $"幂等返回 stable_id={stableId} practice={phraseIndex}");
            return true;
        }
        int candidateIndex = FindQuarantinedCandidateIndex(candidateId);
        if (candidateIndex < 0) return false;

        QuarantinedSingingCandidate record = m_QuarantinedSingingCandidates[candidateIndex];
        PracticePhrase candidate = record.Phrase;
        if (confirmSource && !record.SourceConfirmed)
        {
            record.SourceConfirmed = true;
            candidate.ConfirmedAtRealtime = Time.realtimeSinceStartup;
        }
        candidate.PendingConfirmation = !record.SourceConfirmed;
        candidate.OriginCandidateId = record.CandidateId;
        playbackStatus = string.IsNullOrWhiteSpace(record.PlaybackStatus)
            ? "unavailable" : record.PlaybackStatus;
        if (playbackStatus != "ready" || candidate.WavBytes == null ||
            candidate.WavBytes.Length <= 44 ||
            !HasPlayablePitchTimeline(candidate.MidiTimeline))
        {
            Debug.Log($"[SenseVoice/Quarantine] candidate={candidateId} " +
                      $"source={(record.SourceConfirmed ? "confirmed_user" : "pending")} playback={playbackStatus}，" +
                      "原始证据继续保留，未写入可播放练唱清单");
            return true;
        }

        // Confirmation is not a new performance. Preserve the original recording time.
        phraseIndex = StorePracticeCapture(candidate);
        m_LastCommittedPracticeSignature = candidate.Signature;
        m_LastPracticeCommitTime = Time.realtimeSinceStartup;
        m_QuarantinedSingingCandidates.RemoveAt(candidateIndex);
        Debug.Log($"[SenseVoice/Quarantine] LLM 选用 candidate={candidateId}，source={(record.SourceConfirmed ? "confirmed_user" : "pending")}；" +
                  $"已提交 practice={phraseIndex}，剩余候选=" +
                  m_QuarantinedSingingCandidates.Count);
        return true;
    }

    public bool DiscardQuarantinedSingingCandidate(int candidateId)
    {
        int candidateIndex = FindQuarantinedCandidateIndex(candidateId);
        if (candidateIndex < 0) return false;
        PracticePhrase rejected = m_QuarantinedSingingCandidates[candidateIndex].Phrase;
        if (rejected != null)
        {
            m_RejectedQuarantineSignatures.Add(rejected.Signature);
            if (rejected.CaptureSessionSerial > 0)
                m_RejectedQuarantineSessionSerials.Add(
                    rejected.CaptureSessionSerial);
            //兼容升级前或异常中断留下的状态：若通用 recent_turn 仍恰好指向
            //被否认的同一录音，否认动作必须同时撤销该别名，不能只删列表项。
            bool sameCurrentSession = rejected.CaptureSessionSerial > 0 &&
                m_LastSingingCacheCaptureSessionSerial ==
                    rejected.CaptureSessionSerial;
            byte[] currentIdentityWav = m_LastSingingCacheEvidence != null &&
                m_LastSingingCacheEvidence.RawWavBytes != null
                    ? m_LastSingingCacheEvidence.RawWavBytes
                    : m_LastSingingAudioBytes;
            bool sameCurrentSignature = ComputePracticeSignature(
                currentIdentityWav, null) == rejected.Signature;
            if (HasCurrentSingingPerformanceCandidate() &&
                (sameCurrentSession || sameCurrentSignature))
                RevokeCurrentSingingPlaybackAlias(
                    $"candidate={candidateId} 已被用户否认来源");
        }
        m_QuarantinedSingingCandidates.RemoveAt(candidateIndex);
        Debug.Log($"[SenseVoice/Quarantine] 用户语义否认 candidate={candidateId} 为自己的歌声；" +
                  $"已丢弃，剩余候选={m_QuarantinedSingingCandidates.Count}");
        return true;
    }

    private int FindQuarantinedCandidateIndex(int candidateId)
    {
        if (candidateId <= 0) return -1;
        for (int i = 0; i < m_QuarantinedSingingCandidates.Count; i++)
            if (m_QuarantinedSingingCandidates[i].CandidateId == candidateId) return i;
        return -1;
    }

    private void RemoveQuarantinedCandidateCapturedAt(float capturedAt)
    {
        for (int i = m_QuarantinedSingingCandidates.Count - 1; i >= 0; i--)
        {
            PracticePhrase phrase = m_QuarantinedSingingCandidates[i].Phrase;
            if (phrase != null && phrase.AtRealtime == capturedAt)
                m_QuarantinedSingingCandidates.RemoveAt(i);
        }
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
    /// 有两条可靠入口：辅助 LLM 结合提问语境判定用户明确口头确认；或者这一段真的
    /// 被唱出去、用户听完没有异议。这里仅更新素材事实，不决定角色是否应当演唱。
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
    /// 稳定身份版删除。跨轮修正不得依赖会前移的清单段号；界面仍可显示 order，
    /// 但真正的破坏性操作优先使用 stable_id。
    /// </summary>
    public bool DropPracticePhraseByStableId(
        int stableId, out string dropped, out int remaining, out string failure)
    {
        int index = FindPracticeIndexByStableId(stableId);
        if (index < 0)
        {
            dropped = "";
            remaining = m_PracticePhrases.Count;
            failure = $"没有 stable_id={stableId} 的练唱素材；清单可能已经变化";
            return false;
        }
        return DropPracticePhrase(index + 1, out dropped, out remaining, out failure);
    }

    public bool HasQuarantinedSingingCandidate(int candidateId)
    {
        return FindQuarantinedCandidateIndex(candidateId) >= 0;
    }

    /// <summary>
    /// 可确认身份包括当前隔离候选，以及已经确认并消费进 practice 的历史候选。
    /// 后者用于把 LLM 的重复确认变成幂等 no-op；从未存在的编号仍必须失败。
    /// </summary>
    public bool HasKnownSingingCandidate(int candidateId)
    {
        return FindQuarantinedCandidateIndex(candidateId) >= 0 ||
            TryResolveAdmittedSingingCandidate(candidateId, out _, out _);
    }

    public bool TryResolveConfirmedSingingCandidate(
        int candidateId, out int stableId, out int phraseIndex)
    {
        if (TryResolveAdmittedSingingCandidate(candidateId, out stableId, out phraseIndex) &&
            !m_PracticePhrases[phraseIndex - 1].PendingConfirmation) return true;
        stableId = 0;
        phraseIndex = 0;
        return false;
    }

    private bool TryResolveAdmittedSingingCandidate(
        int candidateId, out int stableId, out int phraseIndex)
    {
        stableId = 0;
        phraseIndex = 0;
        if (candidateId <= 0) return false;
        for (int i = 0; i < m_PracticePhrases.Count; i++)
        {
            PracticePhrase phrase = m_PracticePhrases[i];
            if (phrase == null || phrase.OriginCandidateId != candidateId) continue;
            stableId = phrase.StableId;
            phraseIndex = i + 1;
            return stableId > 0;
        }
        return false;
    }

    /// <summary>Resolve a ready practice identity without confusing it with candidate_id.</summary>
    public bool TryResolvePracticeStableId(int stableId, out int phraseIndex)
    {
        int index = FindPracticeIndexByStableId(stableId);
        phraseIndex = index >= 0 ? index + 1 : 0;
        return index >= 0;
    }

    private int FindPracticeIndexByStableId(int stableId)
    {
        if (stableId <= 0) return -1;
        for (int i = 0; i < m_PracticePhrases.Count; i++)
            if (m_PracticePhrases[i].StableId == stableId) return i;
        return -1;
    }

    /// <summary>
    /// 从不可变的 clean/expanded 源创建新的当前可播放版本。只有解码、裁剪和旋律
    /// 时间线全部成功后才原子替换；原录音继续保留，可再次修边或恢复。
    /// </summary>
    public bool TryRevisePracticePhrase(
        int stableId,
        string capture,
        float trimHeadSeconds,
        float trimTailSeconds,
        bool excludeSpeech,
        out string result,
        out string failure,
        float minimumSeconds = k_MinSingablePerformanceSeconds)
    {
        result = "";
        failure = "";
        int index = FindPracticeIndexByStableId(stableId);
        if (index < 0)
        {
            failure = $"没有 stable_id={stableId} 的练唱素材；没有修改任何内容";
            return false;
        }
        PracticePhrase phrase = m_PracticePhrases[index];
        InitializePracticeSource(phrase);
        if (phrase.LatestRecordingEvidence != null &&
            !ReferenceEquals(phrase.LatestRecordingEvidence, phrase.RecordingEvidence))
        {
            if (!TryPrepareEvidenceRange(phrase, phrase.LatestRecordingEvidence, capture,
                    trimHeadSeconds, trimTailSeconds, out var prepared, out failure)) return false;
            if (prepared.Seconds < minimumSeconds)
            { failure = $"所选范围不足 {minimumSeconds:F1} 秒；当前版本保持不变。"; return false; }
            if (!ValidatePreparedEvidenceRange(index, prepared, excludeSpeech, out failure)) return false;
            m_PracticePhrases[index] = prepared;
            result = $"stable_id={stableId} 已按完整录音证据准备 {prepared.ActiveCapture}，" +
                $"audio={prepared.Seconds:F2}s revision={prepared.Revision}；尚未播放，来源状态未改变。";
            return true;
        }
        capture = string.IsNullOrWhiteSpace(capture)
            ? "clean" : capture.Trim().ToLowerInvariant();
        if (capture != "clean" && capture != "expanded")
        {
            failure = $"capture=\"{capture}\" 不存在；只能选择 clean 或 expanded";
            return false;
        }
        if (trimHeadSeconds < 0f || trimTailSeconds < 0f ||
            float.IsNaN(trimHeadSeconds) || float.IsNaN(trimTailSeconds) ||
            float.IsInfinity(trimHeadSeconds) || float.IsInfinity(trimTailSeconds))
        {
            failure = "trim_head_seconds / trim_tail_seconds 必须是非负有限秒数";
            return false;
        }
        if (!TryValidatePracticeBoundarySelection(
                "stable:" + stableId,
                capture == "expanded",
                trimHeadSeconds,
                trimTailSeconds,
                excludeSpeech,
                out string boundaryConflict))
        {
            failure = boundaryConflict;
            return false;
        }

        byte[] sourceWav = capture == "expanded"
            ? phrase.SourceExpandedWavBytes : phrase.SourceCleanWavBytes;
        float[] sourceTimeline = capture == "expanded"
            ? phrase.SourceExpandedMidiTimeline : phrase.SourceCleanMidiTimeline;
        if (sourceWav == null || sourceWav.Length <= 44 ||
            !HasPlayablePitchTimeline(sourceTimeline))
        {
            failure = $"stable_id={stableId} 没有可用的 {capture} 原始音频与旋律；" +
                      "没有修改当前版本";
            return false;
        }
        if (!TryDecodePcmWav(sourceWav, out float[] samples, out int sampleRate))
        {
            failure = $"stable_id={stableId} 的 {capture} 原始音频不是可编辑的 PCM WAV";
            return false;
        }

        float duration = samples.Length / (float)Mathf.Max(1, sampleRate);
        float start = Mathf.Clamp(trimHeadSeconds, 0f, duration);
        float end = Mathf.Clamp(duration - trimTailSeconds, start, duration);
        if (end - start < minimumSeconds)
        {
            failure = $"指定裁剪后只剩 {end - start:F2}s，短于可播放下限 " +
                      $"{minimumSeconds:F1}s；当前版本保持不变";
            return false;
        }
        int sampleStart = Mathf.Clamp(
            Mathf.RoundToInt(start * sampleRate), 0, samples.Length);
        int sampleEnd = Mathf.Clamp(
            Mathf.RoundToInt(end * sampleRate), sampleStart, samples.Length);
        var croppedSamples = new float[sampleEnd - sampleStart];
        Array.Copy(samples, sampleStart, croppedSamples, 0, croppedSamples.Length);

        float frameSeconds = Mathf.Clamp(phrase.FrameSeconds, 0.02f, 0.25f);
        int frameStart = Mathf.Clamp(
            Mathf.FloorToInt(start / frameSeconds), 0, sourceTimeline.Length);
        int frameEnd = Mathf.Clamp(
            Mathf.CeilToInt(end / frameSeconds), frameStart, sourceTimeline.Length);
        float[] croppedTimeline = SliceFloatArray(sourceTimeline, frameStart, frameEnd);
        if (!HasPlayablePitchTimeline(croppedTimeline))
        {
            failure = "指定裁剪范围内没有可执行的旋律时间线；当前版本保持不变";
            return false;
        }
        byte[] revisedWav = EncodeMonoPcm16Wav(croppedSamples, sampleRate);
        if (revisedWav == null || revisedWav.Length <= 44)
        {
            failure = "修订版本编码失败；当前版本保持不变";
            return false;
        }

        // Selecting the same audio/window is idempotent, including clean and
        // expanded aliases. Keep revision and downstream measured pitch intact.
        var original = phrase.RecordingEvidence;
        float oldOrigin = original == null ? 0f : phrase.ActiveCapture == "expanded"
            ? original.RecoveryStartSeconds : original.CleanStartSeconds;
        float newOrigin = original == null ? 0f : capture == "expanded"
            ? original.RecoveryStartSeconds : original.CleanStartSeconds;
        bool sameCoordinates = (original != null || capture == phrase.ActiveCapture) &&
            Mathf.Abs(oldOrigin + phrase.ActiveTrimHeadSeconds - (newOrigin + start)) < .00001f;
        bool sameAudio = phrase.WavBytes != null && phrase.WavBytes.SequenceEqual(revisedWav);
        // The original may have a different WAV header or not yet have passed
        // through PCM16 re-encoding. Compare decoded samples before quantization
        // too, without treating approximately similar recordings as identical.
        if (sameCoordinates && !sameAudio && phrase.WavBytes != null &&
            TryDecodePcmWav(phrase.WavBytes, out float[] currentSamples, out int currentRate))
            sameAudio = currentRate == sampleRate && currentSamples.SequenceEqual(croppedSamples);
        if (sameCoordinates && sameAudio && phrase.MidiTimeline != null &&
            phrase.MidiTimeline.SequenceEqual(croppedTimeline))
        {
            result = $"stable_id={stableId} 所选范围与当前版本相同；revision={phrase.Revision} 保持，未重新修订或播放。";
            return true;
        }
        //直到这里都只操作局部副本；以下赋值是唯一提交点。
        phrase.WavBytes = revisedWav;
        phrase.MidiTimeline = croppedTimeline;
        phrase.Seconds = GetWavDurationSeconds(revisedWav);
        phrase.ActiveCapture = capture;
        phrase.ActiveTrimHeadSeconds = start;
        phrase.ActiveTrimTailSeconds = Mathf.Max(0f, duration - end);
        phrase.Revision++;
        result = $"stable_id={stableId}（当前清单第 {index + 1} 段）已生成修订版本 v{phrase.Revision}：" +
                 $"source={capture}, trim_head={phrase.ActiveTrimHeadSeconds:F2}s, " +
                 $"trim_tail={phrase.ActiveTrimTailSeconds:F2}s, playable={phrase.Seconds:F2}s。" +
                 "原始 clean/expanded 仍保留，可继续重切；这次没有删除素材。" +
                 $"要试听这个修订版本，请使用 source=practice、order=\"stable:{stableId}\"；" +
                 "source=recent_turn 仍指向录音当时的临时原始素材。";
        Debug.Log("[SenseVoice/Practice] " + result);
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
    public bool TryValidatePracticeBoundarySelection(
        string order,
        bool useExpandedCapture,
        float trimHeadSeconds,
        float trimTailSeconds,
        bool excludeSpeech,
        out string conflict)
    {
        conflict = "";
        List<int> sequence = ParsePracticeOrder(order, out string orderFailure);
        if (sequence == null && string.IsNullOrWhiteSpace(order))
        {
            sequence = new List<int>(m_PracticePhrases.Count);
            for (int i = 0; i < m_PracticePhrases.Count; i++) sequence.Add(i);
        }
        if (sequence == null || sequence.Count == 0)
        {
            conflict = string.IsNullOrWhiteSpace(orderFailure)
                ? "没有解析出要检查边界的练唱段落" : orderFailure;
            return false;
        }

        //clean 与 expanded 各自从 0 秒开始。head_extra / tail_extra 是 expanded
        //相对 clean 多出来的外缘，已经不在 clean 内。若请求在 clean 上又恰好裁掉
        //同样的时长，通常是把两套坐标混用；不擅自改写意图，只把冲突证据退给 LLM。
        if (!useExpandedCapture &&
            (trimHeadSeconds > 0.001f || trimTailSeconds > 0.001f))
        {
            var duplicatedMargins = new List<string>();
            foreach (int index in sequence)
            {
                PracticePhrase phrase = m_PracticePhrases[index];
                var parts = new List<string>();
                if (LooksLikeExpandedMarginUsedAsCleanTrim(
                        trimHeadSeconds, phrase.HeadExtraSeconds))
                    parts.Add($"trim_head={trimHeadSeconds:F2}s 恰好等于 " +
                              $"head_extra={phrase.HeadExtraSeconds:F2}s" +
                              FormatBoundaryEvidence(
                                  phrase.HeadExtraType, phrase.HeadExtraText));
                if (LooksLikeExpandedMarginUsedAsCleanTrim(
                        trimTailSeconds, phrase.TailExtraSeconds))
                    parts.Add($"trim_tail={trimTailSeconds:F2}s 恰好等于 " +
                              $"tail_extra={phrase.TailExtraSeconds:F2}s" +
                              FormatBoundaryEvidence(
                                  phrase.TailExtraType, phrase.TailExtraText));
                if (parts.Count > 0)
                    duplicatedMargins.Add(
                        $"第 {index + 1} 段(stable_id={phrase.StableId}) " +
                        string.Join("；", parts));
            }
            if (duplicatedMargins.Count > 0)
            {
                conflict = "疑似混用了 clean/expanded 的独立时间坐标：" +
                           string.Join("；", duplicatedMargins) +
                           "。head_extra/tail_extra 位于原始 clean 之外，原始 clean 已经排除它们；" +
                           "trim_* 始终从所选 capture 自己的 0 秒边界向内裁剪。" +
                           "程序没有修改或播放素材；若想把外缘纳入后再裁，请选 expanded；" +
                           "若确实想继续裁 clean 内部，请根据 clean 本地坐标重新决定，也可以先询问用户。";
                return false;
            }
        }

        if (!excludeSpeech) return true;
        if ((trimHeadSeconds > 0.001f || trimTailSeconds > 0.001f) &&
            sequence.Count != 1)
        {
            conflict = "exclude_speech=true 且指定秒数裁剪时，目前必须只选择一个 order；" +
                       "多段各自边界不同，程序不会把同一秒数机械套用。";
            return false;
        }

        var collisions = new List<string>();
        foreach (int index in sequence)
        {
            PracticePhrase phrase = m_PracticePhrases[index];
            //聚合 probe 写着 speech、但连续旋律窗触发 review 时，标签已经不是“明确口语”。
            //LLM 选择 expanded 正是在裁决这份冲突；程序不得再拿旧聚合标签否决它。
            //没有冲突的 speech 边界仍保留原校验，防止口语被无意回唱。
            bool selectedExpanded = useExpandedCapture || phrase.ActiveCapture == "expanded";
            float selectedHeadTrim = trimHeadSeconds + (useExpandedCapture ? 0f : phrase.ActiveTrimHeadSeconds);
            float selectedTailTrim = trimTailSeconds + (useExpandedCapture ? 0f : phrase.ActiveTrimTailSeconds);
            bool headSpeech = selectedExpanded && string.Equals(
                phrase.HeadExtraType, "speech", StringComparison.OrdinalIgnoreCase) &&
                !phrase.HeadExtraReviewRequired &&
                phrase.HeadExtraSeconds - selectedHeadTrim > 0.10f;
            bool tailSpeech = selectedExpanded && string.Equals(
                phrase.TailExtraType, "speech", StringComparison.OrdinalIgnoreCase) &&
                !phrase.TailExtraReviewRequired &&
                phrase.TailExtraSeconds - selectedTailTrim > 0.10f;
            if (!headSpeech && !tailSpeech) continue;
            var parts = new List<string>();
            if (headSpeech)
                parts.Add($"head_extra={phrase.HeadExtraSeconds:F2}s speech " +
                          $"转写=\"{(phrase.HeadExtraText ?? "").Trim()}\"");
            if (tailSpeech)
                parts.Add($"tail_extra={phrase.TailExtraSeconds:F2}s speech " +
                          $"转写=\"{(phrase.TailExtraText ?? "").Trim()}\"");
            collisions.Add($"第 {index + 1} 段 " + string.Join("；", parts));
        }
        if (collisions.Count == 0) return true;
        conflict = "本次 LLM 语义目标是排除口语(exclude_speech=true)，但选择的 " +
                   (useExpandedCapture ? "expanded" : "clean") +
                   " 范围仍与明确的口语证据重叠：" + string.Join("；", collisions) +
                   "。程序尚未播放；请" +
                   (useExpandedCapture ? "改用 clean、" : "") +
                   "补足明确裁剪，或结合语境重新决定。";
        return false;
    }

    private static bool LooksLikeExpandedMarginUsedAsCleanTrim(
        float requestedTrimSeconds, float expandedMarginSeconds)
    {
        if (requestedTrimSeconds < 0.10f || expandedMarginSeconds < 0.10f)
            return false;
        float tolerance = Mathf.Max(0.08f, expandedMarginSeconds * 0.03f);
        return Mathf.Abs(requestedTrimSeconds - expandedMarginSeconds) <= tolerance;
    }

    private static string FormatBoundaryEvidence(string type, string text)
    {
        string evidence = string.IsNullOrWhiteSpace(type) ? "" : $"/type={type.Trim()}";
        string transcript = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (transcript.Length > 48) transcript = transcript.Substring(0, 48) + "…";
        if (transcript.Length > 0) evidence += $"/转写=\"{transcript}\"";
        return evidence;
    }

    public bool TryBuildSingingPracticeComposition(
        int performanceSeed,
        float maxSeconds,
        out PracticeComposition composition,
        out string failure,
        string order = "",
        bool useExpandedCapture = false,
        float trimHeadSeconds = 0f,
        float trimTailSeconds = 0f)
    {
        composition = null;
        failure = "";
        if (m_PracticePhrases.Count == 0)
        {
            failure = "练唱会话里还没有任何片段，没有可以唱的东西";
            return false;
        }

        List<int> sequence = ParsePracticeOrder(order, out string orderFailure);
        //原来这里卡的是**库存量** < 2。于是会话里只有一段时，就算用户指名要那一段
        //也一律拒绝——而三段的会话里 order="3" 唱单段却是正常工作的，同样是唱一段，
        //两种结果。真正该卡的是"这次请求解析出来的序列是不是空的"。
        if (sequence == null && !string.IsNullOrWhiteSpace(order))
        {
            //给了 order 却一个合法段号都没有：原来会静默退回"全唱"，
            //她写错段号时用户听到的是一整串，而工具结果还报成功。
            //order 现在也接受歌词片段，所以失败原因要说清是哪一项、为什么。
            failure = $"order=\"{order.Trim()}\" 没有解析出任何段落" +
                      (string.IsNullOrEmpty(orderFailure) ? "：" : "——" + orderFailure + "。") +
                      $"练唱会话现在有 {m_PracticePhrases.Count} 段" +
                      $"(可以写段号 1~{m_PracticePhrases.Count}、stable:稳定身份、" +
                      "candidate:来源候选ID，也可以直接写那一段的歌词片段)";
            return false;
        }
        if (sequence == null)
        {
            sequence = new List<int>(m_PracticePhrases.Count);
            for (int i = 0; i < m_PracticePhrases.Count; i++) sequence.Add(i);
        }
        if (!string.IsNullOrEmpty(orderFailure)) m_LastPracticeOrderProblem = orderFailure;
        if (sequence.Count == 0)
        {
            failure = "这次请求没有解析出任何要唱的段落";
            return false;
        }
        bool hasExplicitTrim = trimHeadSeconds > 0.001f || trimTailSeconds > 0.001f;
        if (hasExplicitTrim && sequence.Count != 1)
        {
            failure = "明确边界裁剪目前只适用于单段；多段不能把同一组秒数机械套用到每段";
            return false;
        }

        var decoded = new List<float[]>(sequence.Count);
        var selectedTimelines = new List<float[]>(sequence.Count);
        int outputRate = 0;
        for (int k = 0; k < sequence.Count; k++)
        {
            int i = sequence[k];
            PracticePhrase stored = m_PracticePhrases[i];
            bool expandedAvailable = useExpandedCapture &&
                stored.RecoveryWavBytes != null && stored.RecoveryWavBytes.Length > 44 &&
                HasPlayablePitchTimeline(stored.RecoveryMidiTimeline);
            if (useExpandedCapture && !expandedAvailable)
            {
                failure = $"第 {i + 1} 段没有可用的 expanded 音频与旋律；" +
                          "程序没有静默改用 clean，请由角色改选 clean 或询问用户";
                return false;
            }
            byte[] selectedWav = expandedAvailable
                ? stored.RecoveryWavBytes : stored.WavBytes;
            float[] selectedTimeline = expandedAvailable
                ? stored.RecoveryMidiTimeline : stored.MidiTimeline;
            if (!TryDecodePcmWav(
                    selectedWav,
                    out float[] phraseSamples,
                    out int phraseRate))
            {
                failure = $"第 {i + 1} 段不是可组合的 PCM WAV";
                return false;
            }
            if (hasExplicitTrim)
            {
                float selectedDuration = phraseSamples.Length / (float)Mathf.Max(1, phraseRate);
                float start = Mathf.Clamp(trimHeadSeconds, 0f, selectedDuration);
                float end = Mathf.Clamp(
                    selectedDuration - trimTailSeconds, start, selectedDuration);
                if (end - start < k_MinSingablePerformanceSeconds)
                {
                    failure = $"指定裁剪后只剩 {end - start:F2}s，短于可播放下限 " +
                              $"{k_MinSingablePerformanceSeconds:F1}s；没有执行";
                    return false;
                }
                int sampleStart = Mathf.Clamp(
                    Mathf.RoundToInt(start * phraseRate), 0, phraseSamples.Length);
                int sampleEnd = Mathf.Clamp(
                    Mathf.RoundToInt(end * phraseRate), sampleStart, phraseSamples.Length);
                var croppedSamples = new float[sampleEnd - sampleStart];
                Array.Copy(phraseSamples, sampleStart, croppedSamples, 0, croppedSamples.Length);
                phraseSamples = croppedSamples;

                float sourceFrameSeconds = Mathf.Clamp(stored.FrameSeconds, 0.02f, 0.25f);
                int frameStart = Mathf.Clamp(
                    Mathf.FloorToInt(start / sourceFrameSeconds), 0,
                    selectedTimeline == null ? 0 : selectedTimeline.Length);
                int frameEnd = Mathf.Clamp(
                    Mathf.CeilToInt(end / sourceFrameSeconds), frameStart,
                    selectedTimeline == null ? 0 : selectedTimeline.Length);
                selectedTimeline = SliceFloatArray(selectedTimeline, frameStart, frameEnd);
                if (!HasPlayablePitchTimeline(selectedTimeline))
                {
                    failure = "指定裁剪范围内没有可执行的旋律时间线；没有执行";
                    return false;
                }
            }
            if (outputRate <= 0) outputRate = phraseRate;
            if (phraseRate != outputRate)
                phraseSamples = ResampleToRate(phraseSamples, phraseRate, outputRate);
            decoded.Add(phraseSamples);
            selectedTimelines.Add(selectedTimeline);
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
        var segmentSources = new List<int>(decoded.Count);

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
            segmentMedians.Add(MedianVoicedPitch(selectedTimelines[i]));
            segmentSources.Add(sequence[i] + 1);

            int src = sequence[i];
            output.AddRange(phrase);
            AppendResampledTimeline(
                midi,
                selectedTimelines[i],
                m_PracticePhrases[src].FrameSeconds,
                outputFrameSeconds,
                pace);
            if (string.IsNullOrEmpty(language) &&
                !string.IsNullOrEmpty(m_PracticePhrases[src].Language))
                language = m_PracticePhrases[src].Language;
            variation.Append($" p{src + 1}={pace:F3}/{gainStart:F2}->{gainEnd:F2}" +
                             (useExpandedCapture &&
                              m_PracticePhrases[src].RecoveryWavBytes != null
                                 ? "/expanded" : ""));
        }

        float duration = output.Count / (float)Mathf.Max(1, outputRate);
        //maxSeconds <= 0 表示不限制整次演唱总长。长歌由调用方按自然段拆成独立
        //转换块；这里仍然完整保留开头和顺序，不再因为总长而整次拒绝。
        if (maxSeconds > 0f && duration > maxSeconds + 0.02f)
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
            SegmentSourceIndices = segmentSources,
            MedianMidi = MedianVoicedPitch(midi.ToArray()),
        };
        return composition.WavBytes != null && composition.WavBytes.Length > 44 &&
            HasPlayablePitchTimeline(composition.MidiTimeline);
    }

    /// <summary>
    /// 把一条 PCM WAV 拆成不超过 <paramref name="maxChunkSeconds"/> 的流式转换块。
    /// 有旋律时间轴时优先在靠近块尾的静音处切；没有时才按时长硬切。所有采样恰好
    /// 出现一次，因此它只改变推理粒度，不裁歌、不重叠，也不改变总时长。
    /// </summary>
    public bool TryBuildSingingStreamChunks(
        byte[] wavBytes,
        float[] midiTimeline,
        float frameSeconds,
        float maxChunkSeconds,
        out PracticeComposition composition,
        out string failure)
    {
        composition = null;
        failure = "";
        if (!TryDecodePcmWav(wavBytes, out float[] samples, out int sampleRate) ||
            samples == null || samples.Length == 0)
        {
            failure = "源歌声不是可分块的 PCM WAV";
            return false;
        }

        float chunkLimit = Mathf.Max(3f, maxChunkSeconds);
        int maxChunkSamples = Mathf.Max(1, Mathf.FloorToInt(chunkLimit * sampleRate));
        float safeFrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f);
        var chunks = new List<byte[]>();
        var gaps = new List<float>();
        var medians = new List<float>();
        var sources = new List<int>();
        int startSample = 0;
        while (startSample < samples.Length)
        {
            int hardEnd = Mathf.Min(samples.Length, startSample + maxChunkSamples);
            int endSample = hardEnd;
            if (hardEnd < samples.Length && midiTimeline != null && midiTimeline.Length > 0)
            {
                endSample = FindStreamingSplitSample(
                    midiTimeline,
                    safeFrameSeconds,
                    sampleRate,
                    startSample,
                    hardEnd);
            }
            //极短尾块既增加固定推理开销又容易爆音；找不到可靠静音点时按上限硬切。
            if (endSample <= startSample + sampleRate / 2 || endSample > hardEnd)
                endSample = hardEnd;

            int count = endSample - startSample;
            var chunkSamples = new float[count];
            Array.Copy(samples, startSample, chunkSamples, 0, count);
            //只有真正位于连续有声区的硬切才需要极短淡入淡出；静音切点上的处理不可闻。
            ApplyShortEdgeFade(chunkSamples, sampleRate, 0.006f);
            chunks.Add(EncodeMonoPcm16Wav(chunkSamples, sampleRate));
            gaps.Add(0f);
            sources.Add(chunks.Count);

            float startSeconds = startSample / (float)sampleRate;
            float endSeconds = endSample / (float)sampleRate;
            int frameStart = Mathf.Clamp(
                Mathf.FloorToInt(startSeconds / safeFrameSeconds), 0,
                midiTimeline == null ? 0 : midiTimeline.Length);
            int frameEnd = Mathf.Clamp(
                Mathf.CeilToInt(endSeconds / safeFrameSeconds), frameStart,
                midiTimeline == null ? 0 : midiTimeline.Length);
            if (midiTimeline != null && frameEnd > frameStart)
            {
                var slice = new float[frameEnd - frameStart];
                Array.Copy(midiTimeline, frameStart, slice, 0, slice.Length);
                medians.Add(MedianVoicedPitch(slice));
            }
            else medians.Add(0f);
            startSample = endSample;
        }

        composition = new PracticeComposition
        {
            WavBytes = wavBytes,
            MidiTimeline = midiTimeline ?? new float[0],
            FrameSeconds = safeFrameSeconds,
            PhraseCount = chunks.Count,
            DurationSeconds = samples.Length / (float)Mathf.Max(1, sampleRate),
            SegmentWavs = chunks,
            Gaps = gaps,
            SegmentMedians = medians,
            SegmentSourceIndices = sources,
            MedianMidi = MedianVoicedPitch(midiTimeline),
        };
        return chunks.Count > 0;
    }

    private static int FindStreamingSplitSample(
        float[] timeline,
        float frameSeconds,
        int sampleRate,
        int startSample,
        int hardEndSample)
    {
        float startSeconds = startSample / (float)Mathf.Max(1, sampleRate);
        float hardEndSeconds = hardEndSample / (float)Mathf.Max(1, sampleRate);
        int firstFrame = Mathf.Clamp(
            Mathf.CeilToInt((startSeconds + 3f) / frameSeconds), 0, timeline.Length - 1);
        int hardEndFrame = Mathf.Clamp(
            Mathf.FloorToInt(hardEndSeconds / frameSeconds), firstFrame, timeline.Length - 1);
        //最多往回看 8 秒；这样块不会为了找静音而变得过短。
        int searchStart = Mathf.Max(
            firstFrame,
            hardEndFrame - Mathf.CeilToInt(8f / frameSeconds));
        for (int frame = hardEndFrame; frame >= searchStart; frame--)
        {
            if (timeline[frame] > 1f) continue;
            int runStart = frame;
            int runEnd = frame;
            while (runStart > searchStart && timeline[runStart - 1] <= 1f) runStart--;
            while (runEnd + 1 <= hardEndFrame && timeline[runEnd + 1] <= 1f) runEnd++;
            int middle = (runStart + runEnd + 1) / 2;
            int sample = Mathf.RoundToInt(middle * frameSeconds * sampleRate);
            return Mathf.Clamp(sample, startSample + 1, hardEndSample);
        }
        return hardEndSample;
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
        if (!string.IsNullOrEmpty(RecentSingingMaterialConflict) ||
            !HasPlayablePitchTimeline(m_LastSingingPerformanceMidi) ||
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
        Action<SongMemoryResult> callback,
        string sourceRef = "")
    {
        byte[] selectedAudio = null;
        string materialError = "";
        if (!TryResolveSongSaveAudio(sourceRef, out selectedAudio, out materialError))
        {
            if (callback != null) callback(new SongMemoryResult
            {
                Ok = false,
                Action = "remember",
                Error = materialError,
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
        form.AddBinaryData("audio_file", selectedAudio, "remembered_singing.wav", "audio/wav");
        Debug.Log($"[SongMemory/Source] source_ref={sourceRef} " +
            $"recent_capture={m_LastSingingCacheCaptureSessionSerial} selected_audio={GetWavDurationSeconds(selectedAudio):F2}s");
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
        //0 表示用户明确要求完整演唱，不设整曲总长；正数只用于自主歌唱软限制。
        float submittedMaxSeconds = maxSeconds <= 0f ? 0f : Mathf.Max(3f, maxSeconds);
        form.AddField("max_seconds", submittedMaxSeconds.ToString(
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
                    ErrorCode = response != null && !string.IsNullOrWhiteSpace(response.error_code)
                        ? response.error_code
                        : "transport",
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
                    ErrorCode = "invalid_audio",
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
                    ErrorCode = "no_playable_audio",
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

    private bool TryResolveSongSaveAudio(string sourceRef, out byte[] audio, out string error)
    {
        if ((sourceRef ?? "").StartsWith("clip:", StringComparison.Ordinal))
        {
            if (!TryResolveSingingClip(sourceRef, out int clipStable, out _) || clipStable <= 0)
            { audio = null; error = "所选 clip 未就绪或不存在；未改用最近录音。"; return false; }
            sourceRef = "stable:" + clipStable;
        }
        audio = null;
        error = "";
        string reference = (sourceRef ?? "").Trim();
        if (!string.IsNullOrEmpty(reference) && reference != "recent_turn")
        {
            if (!reference.StartsWith("stable:", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(reference.Substring(7), out int id) ||
                !TryResolvePracticeStableId(id, out int index))
            {
                error = "保存失败：source_ref 必须是当前存在的 stable:N 或 recent_turn；没有改用其它录音。";
                return false;
            }
            PracticePhrase phrase = m_PracticePhrases[index - 1];
            if (phrase.PendingConfirmation || !HasUsableWavPayload(phrase.WavBytes))
            {
                error = $"保存失败：{reference} 尚未确认或没有可用音频。";
                return false;
            }
            audio = (byte[])phrase.WavBytes.Clone();
            return true;
        }
        if (TryGetRecentSingingAudio(out audio)) return true;
        error = !string.IsNullOrEmpty(RecentSingingMaterialConflict)
            ? RecentSingingMaterialConflict
            : "最近没有可保存的歌唱音频；可用 source_ref=stable:N 明确选择已确认素材，或询问用户。";
        return false;
    }

    /// <summary>
    /// 对角色已经生成的歌声音频做无副作用 F0 回测。该请求不经过 ASR，也不会写入
    /// 用户练唱片段或曲库；调用方可以在播放开始后异步使用它，不增加首音等待。
    /// </summary>
    public void AnalyzeRenderedSingingPitch(
        byte[] wavBytes,
        Action<RenderedPitchAnalysisResult> callback)
    {
        if (wavBytes == null || wavBytes.Length <= 44)
        {
            if (callback != null) callback(new RenderedPitchAnalysisResult
            {
                Ok = false,
                Error = "rendered audio is empty",
            });
            return;
        }
        StartCoroutine(SendRenderedPitchAnalysis(wavBytes, callback));
    }

    private IEnumerator SendRenderedPitchAnalysis(
        byte[] wavBytes,
        Action<RenderedPitchAnalysisResult> callback)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData(
            "audio_file", wavBytes, "rendered_singing.wav", "audio/wav");
        form.AddField("lyrics", "");
        form.AddField("language", "");
        form.AddField("pitch_only", "true");

        string url = m_ServerSetting.TrimEnd('/') + "/singing/score";
        using (UnityWebRequest www = UnityWebRequest.Post(url, form))
        {
            www.SetRequestHeader("accept", "application/json");
            www.timeout = 120;
            yield return www.SendWebRequest();

            SingingPitchResponse response = null;
            string responseText = www.downloadHandler != null
                ? www.downloadHandler.text
                : "";
            try
            {
                if (!string.IsNullOrWhiteSpace(responseText))
                    response = JsonUtility.FromJson<SingingPitchResponse>(responseText);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RenderedPitch] JSON 解析失败: " + e.Message);
            }

            if (www.result != UnityWebRequest.Result.Success || response == null ||
                !response.ok || response.pitch_median_hz <= 0f)
            {
                string detail = response != null && !string.IsNullOrWhiteSpace(response.error)
                    ? response.error
                    : (www.error + (string.IsNullOrWhiteSpace(responseText)
                        ? ""
                        : " / " + responseText));
                if (callback != null) callback(new RenderedPitchAnalysisResult
                {
                    Ok = false,
                    Error = detail,
                });
                yield break;
            }

            float centerMidi = 69f + 12f * Mathf.Log(
                response.pitch_median_hz / 440f, 2f);
            if (callback != null) callback(new RenderedPitchAnalysisResult
            {
                Ok = true,
                CenterMidi = centerMidi,
                CenterNote = response.pitch_median_note ?? "",
                LowNote = response.pitch_low_note ?? "",
                HighNote = response.pitch_high_note ?? "",
                Stability = Mathf.Clamp01(response.pitch_stability),
                VoicedRatio = Mathf.Clamp01(response.voiced_ratio),
                Backend = response.pitch_backend ?? "",
                Error = response.error ?? "",
            });
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

    public sealed class RenderedPitchAnalysisResult
    {
        public bool Ok = false;
        public float CenterMidi = 0f;
        public string CenterNote = "";
        public string LowNote = "";
        public string HighNote = "";
        public float Stability = 0f;
        public float VoicedRatio = 0f;
        public string Backend = "";
        public string Error = "";
    }

    [Serializable]
    private class SingingPitchResponse
    {
        public bool ok = false;
        public string error = "";
        public string pitch_backend = "";
        public float pitch_stability = 0f;
        public float voiced_ratio = 0f;
        public float pitch_median_hz = 0f;
        public string pitch_median_note = "";
        public string pitch_low_note = "";
        public string pitch_high_note = "";
        public SingingScore singing_score = null;
    }

    [Serializable]
    private class AsrTiming
    {
        public float queue_wait;
        public float total, decode, vad, quick_pitch, speaker, recognizer, full_pitch;
        public float recovery, segment_asr, tail_asr, song_recall, dump, other;
    }

    [Serializable]
    private class Response
    {
        public int turn_segments_schema;
        public TurnTranscriptSegment[] turn_segments;
        public string whole_text, segmented_text, transcript_source;
        public bool turn_segments_complete, turn_segments_review_required;
        public bool cancelled;
        public AsrTiming timings;
        public int timing_schema;
        public string text = "";
        public string language = "";
        public string emotion = "";
        public string audio_event = "";
        public string speaker_id = "";
        public string speaker_name = "";
        public string speaker_kind = "";
        public string speaker_status = "";
        public string speaker_identity_id = "";
        public string speaker_voiceprint_id = "";
        public string speaker_voiceprint_status = "";
        public float speaker_confidence = 0f;
        public float speaker_self_confidence = 0f;
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
        //围绕干净歌唱岛、合并相邻低置信旋律岛后的可恢复边界。默认不播放。
        public float singing_recovery_start_seconds = 0f;
        public float singing_recovery_end_seconds = 0f;
        public float singing_head_extra_start_seconds = 0f;
        public float singing_head_extra_end_seconds = 0f;
        public string singing_head_extra_text = "";
        public string singing_head_extra_type = "none";
        public float singing_head_extra_probability = 0f;
        public bool singing_head_extra_review_required = false;
        public float singing_head_extra_melodic_seconds = 0f;
        public float singing_head_extra_melodic_ratio = 0f;
        public float singing_head_extra_longest_melodic_run_seconds = 0f;
        public string singing_head_extra_review_reason = "";
        public SingingBoundarySubsegment[] singing_head_extra_segments = null;
        public float singing_tail_extra_start_seconds = 0f;
        public float singing_tail_extra_end_seconds = 0f;
        public string singing_tail_extra_text = "";
        public string singing_tail_extra_type = "none";
        public float singing_tail_extra_probability = 0f;
        public bool singing_tail_extra_review_required = false;
        public float singing_tail_extra_melodic_seconds = 0f;
        public float singing_tail_extra_melodic_ratio = 0f;
        public float singing_tail_extra_longest_melodic_run_seconds = 0f;
        public string singing_tail_extra_review_reason = "";
        public SingingBoundarySubsegment[] singing_tail_extra_segments = null;
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
        public float singing_analysis_window_offset_seconds = 0f;
        public float singing_score_window_offset_seconds = 0f;
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
        public string speaker_identity_id = "";
        public string speaker_voiceprint_id = "";
        public string speaker_voiceprint_status = "";
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
        public string SpeakerVoiceprintId = "";
        public string SpeakerVoiceprintStatus = "";
        public float SpeakerConfidence = 0f;
        public float SelfConfidence = 0f;
        public bool IsSinging = false;
        public float SingingProbability = 0f;
    }

    [Serializable]
    public class SpeakerManagementResult
    {
        public bool ok = false;
        public string action = "";
        public string summary = "";
        public string error = "";
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
    private class SongCatalogResponse
    {
        public bool ok = false;
        public int local_song_catalog = 0;
        public SongCatalogEntry[] songs = null;
        public string error = "";
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
        public string error_code = "";
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

    [Serializable]
    public class SongCatalogEntry
    {
        public string song_id = "";
        public string title = "";
        public string artist = "";
        public string display_name = "";
        public bool named = false;
        public int reference_count = 0;
        public int unique_segment_count = 0;
        public int duplicate_variant_count = 0;
        public bool can_continue = false;
        public bool score_available = false;
        public int updated_at = 0;
    }

    public class SongCatalogInspectionResult
    {
        public bool Ok = false;
        public string Error = "";
        public string Query = "";
        public bool IncludeUnnamed = false;
        public int TotalEntries = 0;
        public int NamedEntries = 0;
        public int UnnamedEntries = 0;
        //只按标题大小写/首尾空白归并；同名条目仍可能是不同歌曲，结果会明确提醒角色。
        public int UniqueExactTitleGroups = 0;
        public int MatchedEntries = 0;
        public int Offset = 0;
        public int Limit = 0;
        public bool HasMore = false;
        public int NextOffset = 0;
        public SongCatalogEntry[] Entries = new SongCatalogEntry[0];
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
        public string ErrorCode = "";
        public string Error = "";
    }

    #endregion
}
