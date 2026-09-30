using Newtonsoft.Json;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using static GPTSoVITSTextToSpeech;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Generic;

public class GPTSoVITSFASTAPI : TTS
{
    #region 参数定义
    [Header("参考音频路径（Assets内相对路径、绝对路径或服务端相对路径）")]
    [SerializeField] private string m_ReferWavPath = string.Empty; // 例如: "Model/reference.wav"

    [Header("歌声转换专用参考（留空则复用上面的TTS参考）")]
    [SerializeField] private string m_VoiceConversionReferencePath = string.Empty;
    
    [Header("参考音频的文字内容")]
    [SerializeField] private string m_ReferenceText = ""; // 示例: "我是景元"

    [Header("参考音频的语言")]
    [SerializeField] private Language m_ReferenceTextLan = Language.中文; // "zh"

    [Header("合成音频语言模式（自动模式会跟随AI实际回复）")]
    [SerializeField] private TargetLanguageMode m_TargetLanguageMode = TargetLanguageMode.自动识别;

    [Header("固定语言 / 自动识别失败时的回退语言")]
    [SerializeField] private Language m_TargetTextLan = Language.中文; // "zh"

    [Header("在Console记录自动语言切换")]
    [SerializeField] private bool m_LogLanguageChanges = true;

    [Header("启动时预热中文/日文/英语，并缓存延迟短回应")]
    [SerializeField] private bool m_WarmUpAllTargetLanguages = true;

    [Header("空闲时补齐分场景短回应缓存")]
    [SerializeField] private bool m_WarmUpLatencyFillerVariants = true;

    [Header("流式播放预缓冲（秒）")]
    [SerializeField, Range(0.1f, 2f)] private float m_StreamingPrebufferSeconds = 0.65f;

    [Header("Streaming rebuffer target after an underrun (seconds)")]
    [SerializeField, Range(0.2f, 2f)] private float m_StreamingResumeBufferSeconds = 0.85f;

    [Header("Maximum adaptive prebuffer after an unstable stream (seconds)")]
    [SerializeField, Range(0.5f, 2.5f)] private float m_MaxAdaptivePrebufferSeconds = 1.5f;

    [Header("GPT-SoVITS streaming mode (3 uses fixed, faster chunks)")]
    [SerializeField, Range(2, 3)] private int m_StreamingMode = 3;

    [Header("服务端流式语义块最小长度（越小首包越快，过小可能轻微影响连贯性）")]
    [SerializeField, Range(4, 32)] private int m_MinStreamingChunkLength = 12;

    [Header("单段流式音频最大时长（秒）")]
    [SerializeField, Min(10)] private int m_MaxStreamingClipSeconds = 120;

    private UnityWebRequest m_ActiveStreamingRequest;
    private PcmSpeechRequest m_ActivePcmSpeech;
    private PcmSpeechRequest m_PreparedPcmSpeech;
    private AudioSource m_ActiveStreamingOutput;
    private AudioClip m_ActiveStreamingClip;
    private bool m_StreamCancelled;
    private int m_StreamGeneration;
    private float m_AdaptivePrebufferSeconds;
    private readonly Dictionary<Language, List<LatencyFillerEntry>> m_LatencyFillerClips =
        new Dictionary<Language, List<LatencyFillerEntry>>();
    private readonly Dictionary<string, int> m_LatencyFillerCursors =
        new Dictionary<string, int>();
    private readonly Dictionary<Language, string> m_LastLatencyFillerTexts =
        new Dictionary<Language, string>();
    private UnityWebRequest m_ActiveWarmUpRequest;
    private bool m_IsWarmingUp;
    private bool m_StopWarmUpRequested;
    private UnityWebRequest m_ActivePreparedRequest;
    private int m_PreparedSpeechGeneration;
    #endregion

    private void Awake()
    {
        m_AdaptivePrebufferSeconds = m_StreamingPrebufferSeconds;
        if (string.IsNullOrWhiteSpace(m_PostURL))
        {
            m_PostURL = "http://127.0.0.1:9880/tts";
        }
    }

    public override void Speak(string _msg, Action<AudioClip, string> _callback)
    {
        Speak(new SpeechText(_msg), _callback);
    }

    public override void Speak(SpeechText speech, Action<AudioClip, string> callback)
    {
        CancelWarmUpForRealRequest();
        SpeechText resolved = ResolveSpeech(speech);
        StartCoroutine(GetVoice(resolved.Text, callback, resolved.LanguageCode));
    }

    public override bool SupportsStreamingPlayback => true;
    // A concurrent synthesis can starve the active PCM stream and cause underruns.
    // Prefetch only after its HTTP stream has completed; audio playback may continue.
    public override bool CanPrefetchNextSpeech =>
        (m_ActiveStreamingRequest == null || m_ActiveStreamingRequest.isDone) &&
        (m_ActivePreparedRequest == null || m_ActivePreparedRequest.isDone) &&
        (m_PreparedPcmSpeech == null || m_PreparedPcmSpeech.Disposed);

    public override bool SupportsStreamingLookahead => true;

    public override PreparedSpeechStream PrepareStreamingSpeech(SpeechText speech)
    {
        // Never overlap two model jobs. The previous audio may still be playing
        // locally, but its entire HTTP stream must have arrived before this send.
        if (!CanPrefetchNextSpeech) return null;
        CancelWarmUpForRealRequest();
        SpeechText resolved = ResolveSpeech(speech);
        if (string.IsNullOrWhiteSpace(resolved.Text)) return null;
        var trace = TraceContext?.Child("tts-stream-lookahead");
        TraceContext = null;
        m_PreparedPcmSpeech = BeginPcmRequest(resolved, trace, true);
        return m_PreparedPcmSpeech;
    }

    public override void SpeakPreparedStreamingWithPlaybackGate(
        PreparedSpeechStream prepared, AudioSource output, Action<string> onStarted,
        Action<bool, string, float> onCompleted, Func<StreamingPlaybackPermission> playbackGate)
    {
        var job = prepared as PcmSpeechRequest;
        if (job == null || job.Disposed || !ReferenceEquals(job, m_PreparedPcmSpeech))
        {
            prepared?.Dispose();
            onCompleted?.Invoke(false, string.Empty, 0f);
            return;
        }
        // Detach before cancelling the old player: ownership now belongs to StreamVoice.
        m_PreparedPcmSpeech = null;
        CancelStreaming();
        m_StreamCancelled = false;
        TraceContext = null;
        job.Trace?.Mark("tts.lookahead_adopted");
        StartCoroutine(StreamVoice(job.Speech.Text, output, onStarted, onCompleted,
            playbackGate, m_StreamGeneration, job.Speech.LanguageCode, job));
    }

    public override void SpeakStreaming(
        string text,
        AudioSource output,
        Action<string> onStarted,
        Action<bool, string, float> onCompleted)
    {
        SpeakStreamingWithPlaybackGate(text, output, onStarted, onCompleted, null);
    }

    public override void SpeakStreamingWithPlaybackGate(
        string text, AudioSource output, Action<string> onStarted,
        Action<bool, string, float> onCompleted, Func<StreamingPlaybackPermission> playbackGate)
    {
        SpeakStreamingWithPlaybackGate(new SpeechText(text), output, onStarted, onCompleted, playbackGate);
    }

    public override void SpeakStreamingWithPlaybackGate(
        SpeechText speech, AudioSource output, Action<string> onStarted,
        Action<bool, string, float> onCompleted, Func<StreamingPlaybackPermission> playbackGate)
    {
        CancelWarmUpForRealRequest();
        CancelStreaming();
        m_StreamCancelled = false;
        SpeechText resolved = ResolveSpeech(speech);
        StartCoroutine(StreamVoice(resolved.Text, output, onStarted, onCompleted, playbackGate,
            m_StreamGeneration, resolved.LanguageCode));
    }

    public override void CancelStreaming()
    {
        m_PreparedPcmSpeech?.Dispose();
        m_PreparedPcmSpeech = null;
        m_StreamGeneration++;
        m_StreamCancelled = true;
        if (m_ActiveStreamingRequest != null && !m_ActiveStreamingRequest.isDone)
        {
            m_ActiveStreamingRequest.Abort();
        }
        m_ActiveStreamingRequest = null;
        if (m_ActiveStreamingOutput != null && m_ActiveStreamingClip != null &&
            m_ActiveStreamingOutput.clip == m_ActiveStreamingClip)
        {
            m_ActiveStreamingOutput.Stop();
        }
        m_ActiveStreamingOutput = null;
        m_ActiveStreamingClip = null;
    }

    /// <summary>
    /// 预热：发一条极短的合成请求，触发GPT-SoVITS加载模型到显存。
    /// 之后真实用户请求不会再碰到冷启动(首次合成多花2-4秒)的问题。
    /// 不会向外抛回调，也不会产生任何角色可见的语音。
    /// </summary>
    public override void WarmUp()
    {
        if (m_IsWarmingUp || HasAllRequiredWarmUps()) return;
        if (string.IsNullOrEmpty(m_ReferWavPath))
        {
            Debug.LogWarning("[TTS预热] m_ReferWavPath未配置，跳过");
            return;
        }
        m_StopWarmUpRequested = false;
        StartCoroutine(DoWarmUp());
    }

    private IEnumerator DoWarmUp()
    {
        m_IsWarmingUp = true;
        List<Language> languages = new List<Language> { m_TargetTextLan };
        if (m_WarmUpAllTargetLanguages)
        {
            AddWarmUpLanguageIfMissing(languages, Language.中文);
            AddWarmUpLanguageIfMissing(languages, Language.日文);
            AddWarmUpLanguageIfMissing(languages, Language.英文);
        }

        // 先给每种语言准备一个基础应声，确保启动后尽快可用；随后优先补齐角色当前
        // 输出语言的变体，再利用空闲时间补其他语言。正式回复会随时中止后半段。
        List<LatencyFillerWarmTarget> targets = BuildLatencyFillerWarmUpOrder(languages);
        foreach (LatencyFillerWarmTarget target in targets)
        {
            if (m_StopWarmUpRequested) break;
            LatencyFillerEntry entry = target.Entry;
            if (entry == null || entry.Clip != null) continue;

            RequestData requestData = new RequestData
            {
                ref_audio_path = ResolveReferenceAudioPath(),
                prompt_text = m_ReferenceText,
                prompt_lang = ConvertLanguageEnum(m_ReferenceTextLan),
                text = entry.Text,
                text_lang = ConvertLanguageEnum(target.Language),
                streaming_mode = 0,
                media_type = "wav"
            };
            string postJson = JsonUtility.ToJson(requestData);

            float t0 = Time.realtimeSinceStartup;
            using (UnityWebRequest request = new UnityWebRequest(m_PostURL, "POST"))
            {
                m_ActiveWarmUpRequest = request;
                byte[] data = System.Text.Encoding.UTF8.GetBytes(postJson);
                request.uploadHandler = new UploadHandlerRaw(data);
                request.downloadHandler = new DownloadHandlerAudioClip(m_PostURL, AudioType.WAV);
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 30;

                yield return request.SendWebRequest();

                float dt = Time.realtimeSinceStartup - t0;
                if (!m_StopWarmUpRequested && request.responseCode == 200)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        if (entry.Clip != null) Destroy(entry.Clip);
                        clip.name = "latency-filler-" + ConvertLanguageEnum(target.Language) + "-" + entry.Context;
                        entry.Clip = clip;
                    }
                    Debug.Log($"[TTS预热] {ConvertLanguageEnum(target.Language)}/{entry.Context} " +
                              $"缓存短回应“{entry.Text}”，耗时 {dt:F2}s");
                }
                else if (!m_StopWarmUpRequested)
                {
                    Debug.LogWarning($"[TTS预热] {ConvertLanguageEnum(target.Language)}/{entry.Context} " +
                                     $"失败(code={request.responseCode}): {request.error}");
                }
            }
            m_ActiveWarmUpRequest = null;
        }

        m_ActiveWarmUpRequest = null;
        m_IsWarmingUp = false;
    }

    private static void AddWarmUpLanguageIfMissing(List<Language> languages, Language language)
    {
        if (!languages.Contains(language)) languages.Add(language);
    }

    private List<LatencyFillerWarmTarget> BuildLatencyFillerWarmUpOrder(List<Language> languages)
    {
        var result = new List<LatencyFillerWarmTarget>();
        foreach (Language language in languages) EnsureLatencyFillerEntries(language);

        // 第一轮：每种语言的首个 neutral，保持原有快速预热特性。
        foreach (Language language in languages)
        {
            List<LatencyFillerEntry> entries = m_LatencyFillerClips[language];
            if (entries.Count > 0)
                result.Add(new LatencyFillerWarmTarget(language, entries[0]));
        }
        if (!m_WarmUpLatencyFillerVariants) return result;

        AddRemainingWarmUpTargets(result, m_TargetTextLan);
        foreach (Language language in languages)
        {
            if (language != m_TargetTextLan)
                AddRemainingWarmUpTargets(result, language);
        }
        return result;
    }

    private void AddRemainingWarmUpTargets(
        List<LatencyFillerWarmTarget> result, Language language)
    {
        EnsureLatencyFillerEntries(language);
        List<LatencyFillerEntry> entries = m_LatencyFillerClips[language];
        // 机械重复是最先要解决的问题，因此先补 neutral；唱歌开场次之；thinking 最后。
        string[] priorities = { "neutral", "singing", "thinking" };
        foreach (string context in priorities)
        {
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i].Context == context)
                    result.Add(new LatencyFillerWarmTarget(language, entries[i]));
            }
        }
    }

    private void EnsureLatencyFillerEntries(Language language)
    {
        if (m_LatencyFillerClips.ContainsKey(language)) return;

        var entries = new List<LatencyFillerEntry>();
        switch (language)
        {
            case Language.英文:
                entries.Add(new LatencyFillerEntry("neutral", "Hmm..."));
                entries.Add(new LatencyFillerEntry("neutral", "I see..."));
                entries.Add(new LatencyFillerEntry("neutral", "I'm listening..."));
                entries.Add(new LatencyFillerEntry("thinking", "Let me think..."));
                entries.Add(new LatencyFillerEntry("thinking", "One moment..."));
                entries.Add(new LatencyFillerEntry("singing", "Mm, I was listening..."));
                entries.Add(new LatencyFillerEntry("singing", "That melody stayed with me..."));
                entries.Add(new LatencyFillerEntry("singing", "There's a lovely afterglow..."));
                break;
            case Language.日文:
                entries.Add(new LatencyFillerEntry("neutral", "そうね……"));
                entries.Add(new LatencyFillerEntry("neutral", "うん……"));
                entries.Add(new LatencyFillerEntry("neutral", "なるほど……"));
                entries.Add(new LatencyFillerEntry("thinking", "ええと……"));
                entries.Add(new LatencyFillerEntry("thinking", "ちょっと待ってね……"));
                entries.Add(new LatencyFillerEntry("singing", "ふふっ……"));
                entries.Add(new LatencyFillerEntry("singing", "うん、ちゃんと聴いていたわ……"));
                entries.Add(new LatencyFillerEntry("singing", "まだ余韻が残っているわ……"));
                break;
            case Language.中文:
            default:
                entries.Add(new LatencyFillerEntry("neutral", "嗯……"));
                entries.Add(new LatencyFillerEntry("neutral", "原来如此……"));
                entries.Add(new LatencyFillerEntry("neutral", "我在听……"));
                entries.Add(new LatencyFillerEntry("thinking", "让我想想……"));
                entries.Add(new LatencyFillerEntry("thinking", "稍等一下哦……"));
                entries.Add(new LatencyFillerEntry("singing", "嗯，我有认真听哦……"));
                entries.Add(new LatencyFillerEntry("singing", "这段旋律很特别呢……"));
                entries.Add(new LatencyFillerEntry("singing", "还有些余韵呢……"));
                break;
        }
        m_LatencyFillerClips[language] = entries;
    }

    private bool HasAllRequiredWarmUps()
    {
        if (!HasRequiredWarmUpsForLanguage(m_TargetTextLan)) return false;
        if (!m_WarmUpAllTargetLanguages) return true;
        return HasRequiredWarmUpsForLanguage(Language.中文)
            && HasRequiredWarmUpsForLanguage(Language.日文)
            && HasRequiredWarmUpsForLanguage(Language.英文);
    }

    private bool HasRequiredWarmUpsForLanguage(Language language)
    {
        EnsureLatencyFillerEntries(language);
        List<LatencyFillerEntry> entries = m_LatencyFillerClips[language];
        int required = m_WarmUpLatencyFillerVariants ? entries.Count : Mathf.Min(1, entries.Count);
        for (int i = 0; i < required; i++)
        {
            if (entries[i].Clip == null) return false;
        }
        return required > 0;
    }

    public override void PrioritizeConversation()
    {
        CancelWarmUpForRealRequest();
        CancelPreparedSpeech();
    }

    private void CancelWarmUpForRealRequest()
    {
        if (!m_IsWarmingUp || m_StopWarmUpRequested) return;
        m_StopWarmUpRequested = true;
        if (m_ActiveWarmUpRequest != null && !m_ActiveWarmUpRequest.isDone)
            m_ActiveWarmUpRequest.Abort();
        Debug.Log("[TTS预热] 检测到正式请求，停止剩余预热以避免阻塞回复");
    }

    private void OnDestroy()
    {
        CancelStreaming();
        m_ActivePcmSpeech?.Dispose();
        m_ActivePcmSpeech = null;
        CancelPreparedSpeech();
        foreach (List<LatencyFillerEntry> entries in m_LatencyFillerClips.Values)
        {
            foreach (LatencyFillerEntry entry in entries)
            {
                if (entry != null && entry.Clip != null) Destroy(entry.Clip);
            }
        }
        m_LatencyFillerClips.Clear();
    }

    public override void PrepareSpeech(string text, Action<AudioClip, string> callback)
    {
        PrepareSpeech(new SpeechText(text), callback);
    }

    public override void PrepareSpeech(SpeechText speech, Action<AudioClip, string> callback)
    {
        CancelWarmUpForRealRequest();
        string text = Regex.Replace(speech.Text ?? string.Empty, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        if (string.IsNullOrEmpty(text))
        {
            callback?.Invoke(null, text);
            return;
        }

        CancelPreparedSpeech();
        int generation = ++m_PreparedSpeechGeneration;
        SpeechText resolved = ResolveSpeech(new SpeechText(text, speech.LanguageCode, speech.LanguageSource));
        ConversationTrace trace = TraceContext?.Child("tts-prepared");
        TraceContext = null;
        StartCoroutine(GetPreparedVoice(resolved.Text, generation, callback, resolved.LanguageCode, trace));
    }

    public override void CancelPreparedSpeech()
    {
        m_PreparedPcmSpeech?.Dispose();
        m_PreparedPcmSpeech = null;
        m_PreparedSpeechGeneration++;
        if (m_ActivePreparedRequest != null && !m_ActivePreparedRequest.isDone)
            m_ActivePreparedRequest.Abort();
        m_ActivePreparedRequest = null;
    }

    private IEnumerator GetPreparedVoice(
        string text,
        int generation,
        Action<AudioClip, string> callback, string languageCode,
        ConversationTrace trace)
    {
        RequestData requestData = CreateSpeechRequest(text, languageCode, 0);

        string postJson = JsonUtility.ToJson(requestData);
        float requestStart = Time.realtimeSinceStartup;
        using (UnityWebRequest request = new UnityWebRequest(m_PostURL, "POST"))
        {
            m_ActivePreparedRequest = request;
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(postJson));
            request.downloadHandler = new DownloadHandlerAudioClip(m_PostURL, AudioType.WAV);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 20;

            trace?.Mark("tts.prepare_send", new Newtonsoft.Json.Linq.JObject
            {
                ["characters"] = text.Length,
                ["language"] = languageCode ?? string.Empty
            });
            yield return request.SendWebRequest();

            trace?.Mark("tts.prepare_http_complete", new Newtonsoft.Json.Linq.JObject
            {
                ["httpStatus"] = request.responseCode,
                ["transportResult"] = request.result.ToString(),
                ["elapsedSeconds"] = Time.realtimeSinceStartup - requestStart
            });

            if (generation != m_PreparedSpeechGeneration)
            {
                trace?.Mark("tts.prepare_discarded", new Newtonsoft.Json.Linq.JObject
                {
                    ["reason"] = "generation-changed"
                });
                callback?.Invoke(null, text);
                yield break;
            }

            m_ActivePreparedRequest = null;
            if (request.responseCode == 200)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip != null) clip.name = "prepared-singing-bridge";
                trace?.Mark("tts.prepare_clip_ready", new Newtonsoft.Json.Linq.JObject
                {
                    ["clipReady"] = clip != null,
                    ["durationSeconds"] = clip != null ? clip.length : 0f,
                    ["elapsedSeconds"] = Time.realtimeSinceStartup - requestStart
                });
                callback?.Invoke(clip, text);
                trace?.Mark("tts.prepare_callback");
            }
            else
            {
                Debug.LogWarning($"[歌唱预反应] 静默预合成失败(code={request.responseCode}): {request.error}");
                callback?.Invoke(null, text);
                trace?.Mark("tts.prepare_callback", new Newtonsoft.Json.Linq.JObject
                {
                    ["clipReady"] = false,
                    ["error"] = request.error ?? string.Empty
                });
            }
        }
    }

    private IEnumerator GetVoice(string _msg, Action<AudioClip, string> _callback, string languageCode)
    {
        stopwatch.Restart();

    // ✅ 在这里清除 <think> 标签
    _msg = Regex.Replace(_msg, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();

        RequestData _requestData = CreateSpeechRequest(_msg, languageCode, 0);

        string _postJson = JsonUtility.ToJson(_requestData);

        using (UnityWebRequest request = new UnityWebRequest(m_PostURL, "POST"))
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(_postJson);
            request.uploadHandler = new UploadHandlerRaw(data);
            request.downloadHandler = new DownloadHandlerAudioClip(m_PostURL, AudioType.WAV);

            request.SetRequestHeader("Content-Type", "application/json");
            //硬上限：单段合成正常2-5秒，20秒还没回就当死了，让上游赶紧跳过
            request.timeout = 20;

            yield return request.SendWebRequest();

            if (request.responseCode == 200)
            {
                AudioClip audioClip = ((DownloadHandlerAudioClip)request.downloadHandler).audioClip;
                _callback(audioClip, _msg);
            }
            else
            {
                Debug.LogError("❌ 语音合成失败: " + request.error);
                Debug.LogError("❌ 服务器响应码: " + request.responseCode);
                Debug.LogError("❌ 返回内容: " + request.downloadHandler.text);
                //关键：失败也要回调，否则上游的pendingDone永远不会true，整条流水线会干等60秒
                _callback(null, _msg);
            }
        }
    }

    /// <summary>
    /// api_v2 streaming_mode=2 会先返回一个 WAV 头，后续持续追加 16-bit PCM。
    /// 收到少量预缓冲后立即创建流式 AudioClip，避免等待整句合成完成。
    /// </summary>
    private IEnumerator StreamVoice(
        string text,
        AudioSource output,
        Action<string> onStarted,
        Action<bool, string, float> onCompleted,
        Func<StreamingPlaybackPermission> playbackGate, int generation, string languageCode,
        PcmSpeechRequest prepared = null)
    {
        var trace = prepared != null ? prepared.Trace : TraceContext?.Child("tts-stream");
        TraceContext = null; // Capture once: prewarm/later jobs must not inherit this span.
        bool gateCancelled = false;
        bool playbackInterrupted = false;
        AudioClip streamingClip = null;
        bool IsCurrent()
        {
            if (m_StreamCancelled || generation != m_StreamGeneration || gateCancelled || playbackInterrupted ||
                (prepared != null && prepared.Disposed)) return false;
            if (streamingClip != null && (output == null || output.clip != streamingClip)) return false;
            if (playbackGate != null && playbackGate() == StreamingPlaybackPermission.Cancel)
                gateCancelled = true;
            return !gateCancelled;
        }
        text = Regex.Replace(text ?? string.Empty, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        if (string.IsNullOrEmpty(text) || output == null)
        {
            prepared?.Dispose();
            onCompleted?.Invoke(false, text, 0f);
            yield break;
        }

        var job = prepared ?? BeginPcmRequest(new SpeechText(text, languageCode), trace, false);
        var pcmHandler = job.Pcm;
        bool started = false;
        bool playbackAdvanced = false;
        bool success = false;
        float requestStart = job.StartedAt;

        using (job)
        {
            UnityWebRequest request = job.Request;
            m_ActivePcmSpeech = job;
            m_ActiveStreamingRequest = request;
            UnityWebRequestAsyncOperation operation = job.Operation;

            //等 WAV 头和最小预缓冲。短句若已经完整返回，也立即开始播放。
            while (IsCurrent() && !pcmHandler.HeaderReady && !operation.isDone)
            {
                yield return null;
            }

            if (IsCurrent() && pcmHandler.HeaderReady && !pcmHandler.FormatError)
            {
                float initialPrebuffer = job.PrebufferSeconds;
                int wantedSamples = Mathf.CeilToInt(
                    pcmHandler.SampleRate * pcmHandler.Channels * initialPrebuffer);
                while (IsCurrent() && !operation.isDone && pcmHandler.BufferedSampleCount < wantedSamples)
                {
                    yield return null;
                }

                job.Observe();
                float gateEnteredAt = Time.realtimeSinceStartup;
                // The network request and synthesis have already run. Waiting here
                // leaves the opener's AudioSource untouched while PCM accumulates.
                while (IsCurrent() && playbackGate != null)
                {
                    StreamingPlaybackPermission permission = playbackGate();
                    if (permission == StreamingPlaybackPermission.Cancel) { gateCancelled = true; break; }
                    if (permission == StreamingPlaybackPermission.Play) break;
                    yield return null;
                }
                float gateWaitSeconds = Time.realtimeSinceStartup - gateEnteredAt;

                if (operation.isDone) pcmHandler.MarkInputComplete();

                if (IsCurrent() && pcmHandler.TotalSamplesReceived > 0)
                {
                    m_ActiveStreamingOutput = output;
                    float clipStartedAt = Time.realtimeSinceStartup;
                    streamingClip = AudioClip.Create(
                        "GPT-SoVITS-stream",
                        pcmHandler.SampleRate * Mathf.Max(10, m_MaxStreamingClipSeconds),
                        pcmHandler.Channels,
                        pcmHandler.SampleRate,
                        true,
                        pcmHandler.ReadSamples);
                    trace?.Mark("audio.stream_clip_created", new Newtonsoft.Json.Linq.JObject {
                        ["elapsedSeconds"] = Time.realtimeSinceStartup - clipStartedAt });
                    output.clip = streamingClip;
                    m_ActiveStreamingClip = streamingClip;
                    output.loop = false;
                    output.Play();
                    trace?.Mark("audio.source_play_called");
                    started = true;
                    onStarted?.Invoke(text);
                    Debug.Log($"[TTS/Timing] header={job.HeaderAt - requestStart:F2}s " +
                        $"firstPcm={job.FirstPcmAt - requestStart:F2}s " +
                        $"bufferReady={job.ReadyAt - requestStart:F2}s " +
                        $"playbackGate={gateWaitSeconds:F2}s total={Time.realtimeSinceStartup - requestStart:F2}s");
                    Debug.Log($"[TTS流式] 首批PCM开始播放，等待 {Time.realtimeSinceStartup - requestStart:F2}s，缓冲 {pcmHandler.BufferedSeconds:F2}s");
                }
            }

            //请求结束后不能只看 BufferedSampleCount：Unity 音频线程会提前把 PCM
            //从环形队列读进 DSP 缓冲，此时队列虽空，扬声器可能只播到一半。
            //记录“最后一批真实 PCM 被写入 AudioClip 后”的播放目标位置，再等待
            //AudioSource.timeSamples 真正追上它。
            long drainTargetFrames = -1;
            bool pausedForRebuffer = false;
            float rebufferStartedAt = 0f;
            float rebufferWaitSeconds = 0f;
            int streamUnderruns = 0;
            int rebufferEpisodes = 0;
            long pendingUnderrunFrame = -1;
            AudioSettings.GetDSPBufferSize(out int dspBufferFrames, out _);
            int pauseLeadFrames = Mathf.CeilToInt(dspBufferFrames * pcmHandler.SampleRate /
                (float)Mathf.Max(1, AudioSettings.outputSampleRate));
            while (started && IsCurrent())
            {
                if (!playbackAdvanced && output.isPlaying && output.timeSamples > 0)
                {
                    playbackAdvanced = true;
                    Debug.Log($"[TTS/PlaybackEvidence] phase=playback-advanced generation={generation} samples={output.timeSamples} scope=audio-source-progress-not-human-audibility");
                }
                if (!pausedForRebuffer && !output.isPlaying)
                { playbackInterrupted = true; break; }
                if (operation.isDone) pcmHandler.MarkInputComplete();

                int newUnderruns = pcmHandler.ConsumeUnderrunSnapshot(out long firstUnderrunSample);
                if (newUnderruns > 0)
                {
                    streamUnderruns += newUnderruns;
                    if (!pausedForRebuffer && firstUnderrunSample >= 0)
                    {
                        long gapFrame = firstUnderrunSample / Mathf.Max(1, pcmHandler.Channels);
                        pendingUnderrunFrame = pendingUnderrunFrame < 0
                            ? gapFrame : Math.Min(pendingUnderrunFrame, gapFrame);
                    }
                }

                float resumeBuffer = Mathf.Max(m_StreamingResumeBufferSeconds, job.PrebufferSeconds);
                if (!pausedForRebuffer && pendingUnderrunFrame >= 0)
                {
                    // PCMReaderCallback runs ahead of playback. Let already queued
                    // real speech play while synthesis catches up; never seek over PCM.
                    if (operation.isDone || pcmHandler.BufferedSeconds >= resumeBuffer)
                        pendingUnderrunFrame = -1;
                    else if (output.timeSamples + pauseLeadFrames >= pendingUnderrunFrame)
                    {
                        output.Pause();
                        pausedForRebuffer = true;
                        ++rebufferEpisodes;
                        rebufferStartedAt = Time.realtimeSinceStartup;
                        trace?.Mark("tts.rebuffer_start", new Newtonsoft.Json.Linq.JObject {
                            ["episode"] = rebufferEpisodes, ["bufferSeconds"] = pcmHandler.BufferedSeconds,
                            ["firstGapFrame"] = pendingUnderrunFrame, ["playbackFrame"] = output.timeSamples,
                            ["outputSamplesWritten"] = pcmHandler.TotalOutputSamplesWritten,
                            ["lastRealOutputSample"] = pcmHandler.LastRealOutputSamplePosition,
                            ["pauseLeadFrames"] = pauseLeadFrames });
                        pendingUnderrunFrame = -1;
                        Debug.LogWarning($"[TTS stream] playback approaching PCM gap; pausing for rebuffer (buffer={pcmHandler.BufferedSeconds:F2}s)");
                    }
                }

                if (pausedForRebuffer)
                {
                    bool canResume = operation.isDone ||
                        pcmHandler.BufferedSeconds >= resumeBuffer;
                    if (canResume && output != null)
                    {
                        rebufferWaitSeconds += Time.realtimeSinceStartup - rebufferStartedAt;
                        output.UnPause();
                        pausedForRebuffer = false;
                        trace?.Mark("tts.rebuffer_end", new Newtonsoft.Json.Linq.JObject {
                            ["episode"] = rebufferEpisodes, ["waitSeconds"] = Time.realtimeSinceStartup - rebufferStartedAt,
                            ["bufferSeconds"] = pcmHandler.BufferedSeconds });
                        Debug.Log($"[TTS stream] playback resumed after {Time.realtimeSinceStartup - rebufferStartedAt:F2}s, " +
                                  $"buffer={pcmHandler.BufferedSeconds:F2}s");
                    }
                }

                if (operation.isDone && pcmHandler.BufferedSampleCount == 0)
                {
                    if (drainTargetFrames < 0)
                    {
                        drainTargetFrames = pcmHandler.LastRealOutputSamplePosition / Mathf.Max(1, pcmHandler.Channels);
                    }

                    if (output.timeSamples >= drainTargetFrames)
                    {
                        break;
                    }
                }
                yield return null;
            }

            if (started && IsCurrent() && streamUnderruns > 0)
            {
                // Missing PCM still matters even when download catches up before
                // a pause. Use missing audio duration, not callback frequency.
                float adaptiveIncrease = Mathf.Clamp(pcmHandler.InsertedSilenceSeconds, 0.3f, 0.5f);
                m_AdaptivePrebufferSeconds = Mathf.Min(
                    m_MaxAdaptivePrebufferSeconds,
                    Mathf.Max(m_StreamingPrebufferSeconds, m_AdaptivePrebufferSeconds) +
                    adaptiveIncrease);
                Debug.LogWarning($"[TTS stream] underruns={streamUnderruns}, insertedSilence={pcmHandler.InsertedSilenceSeconds:F3}s, " +
                                 $"rebufferWait={rebufferWaitSeconds:F3}s, " +
                                 $"nextPrebuffer={m_AdaptivePrebufferSeconds:F2}s");
            }
            else if (started && IsCurrent())
            {
                m_AdaptivePrebufferSeconds = Mathf.MoveTowards(
                    Mathf.Max(m_StreamingPrebufferSeconds, m_AdaptivePrebufferSeconds),
                    m_StreamingPrebufferSeconds,
                    0.02f);
            }

            success = IsCurrent()
                && request.result == UnityWebRequest.Result.Success
                && started
                && playbackAdvanced
                && !pcmHandler.FormatError;

            if (!IsCurrent() && !operation.isDone) request.Abort();
            job.Observe();
            trace?.Mark("tts.playback_completed", new Newtonsoft.Json.Linq.JObject {
                ["success"] = success, ["underruns"] = streamUnderruns,
                ["rebufferEpisodes"] = rebufferEpisodes,
                ["maximumCallbackSamples"] = pcmHandler.MaximumCallbackSamples,
                ["callbackSeconds"] = pcmHandler.MaximumCallbackSamples / (float)Mathf.Max(1, pcmHandler.SampleRate * pcmHandler.Channels),
                ["rebufferWaitSeconds"] = rebufferWaitSeconds,
                ["insertedSilenceSeconds"] = pcmHandler.InsertedSilenceSeconds
            });
            if (!success && IsCurrent())
            {
                Debug.LogError($"[TTS流式] 失败(code={request.responseCode}): {request.error}; {pcmHandler.FormatErrorMessage}");
            }
        }

        float audioDuration = pcmHandler.AudioDuration;
        // An obsolete buffered request must never stop or clear a newer stream/opener.
        if (streamingClip != null && output != null && output.clip == streamingClip) output.Stop();
        if (generation == m_StreamGeneration)
        {
            m_ActivePcmSpeech = null;
            m_ActiveStreamingRequest = null;
            m_ActiveStreamingOutput = null;
            m_ActiveStreamingClip = null;
        }
        if (started && !playbackAdvanced)
            Debug.LogWarning($"[TTS/PlaybackEvidence] phase=ended-before-observed-progress generation={generation} success=False");
        onCompleted?.Invoke(success, text, audioDuration);

        if (streamingClip != null)
        {
            if (output != null && output.clip == streamingClip) output.clip = null;
            Destroy(streamingClip);
        }
    }

    private PcmSpeechRequest BeginPcmRequest(SpeechText speech, ConversationTrace trace, bool lookahead)
    {
        float prebuffer = Mathf.Clamp(Mathf.Max(m_StreamingPrebufferSeconds, m_AdaptivePrebufferSeconds),
            0.1f, m_MaxAdaptivePrebufferSeconds);
        var job = new PcmSpeechRequest(m_PostURL,
            JsonUtility.ToJson(CreateSpeechRequest(speech.Text, speech.LanguageCode, m_StreamingMode)),
            speech, trace, prebuffer, lookahead);
        StartCoroutine(ObservePcmRequest(job));
        return job;
    }

    private IEnumerator ObservePcmRequest(PcmSpeechRequest job)
    {
        // Runs even while this is the silent next sentence. No AudioSource is touched.
        while (!job.Disposed)
        {
            job.Observe();
            if (job.Operation.isDone) yield break;
            yield return null;
        }
    }

    private sealed class PcmSpeechRequest : PreparedSpeechStream
    {
        public readonly SpeechText Speech;
        public readonly ConversationTrace Trace;
        public readonly UnityWebRequest Request;
        public readonly UnityWebRequestAsyncOperation Operation;
        public readonly PcmStreamingDownloadHandler Pcm;
        public readonly float StartedAt, PrebufferSeconds;
        public float HeaderAt { get; private set; } = -1f;
        public float FirstPcmAt { get; private set; } = -1f;
        public float ReadyAt { get; private set; } = -1f;
        public bool Disposed { get; private set; }
        private bool received, ready;

        public PcmSpeechRequest(string url, string json, SpeechText speech, ConversationTrace trace,
            float prebufferSeconds, bool lookahead)
        {
            Speech = speech; Trace = trace; PrebufferSeconds = prebufferSeconds;
            StartedAt = Time.realtimeSinceStartup;
            Pcm = new PcmStreamingDownloadHandler();
            Pcm.OnHeader = () => HeaderAt = Time.realtimeSinceStartup;
            Pcm.OnFirstPcm = () => { FirstPcmAt = Time.realtimeSinceStartup; Trace?.Mark("tts.first_pcm"); };
            Request = new UnityWebRequest(url, "POST");
            Request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            Request.downloadHandler = Pcm;
            Request.SetRequestHeader("Content-Type", "application/json");
            Request.timeout = 30;
            Trace?.Mark("tts.send", new Newtonsoft.Json.Linq.JObject {
                ["lookahead"] = lookahead, ["characters"] = speech.Text.Length,
                ["language"] = speech.LanguageCode, ["prebufferSeconds"] = prebufferSeconds
            });
            Operation = Request.SendWebRequest();
        }

        public void Observe()
        {
            if (Disposed) return;
            if (!received && Operation.isDone)
            {
                received = true;
                Pcm.MarkInputComplete();
                Trace?.Mark("tts.receive_complete", new Newtonsoft.Json.Linq.JObject {
                    ["httpStatus"] = Request.responseCode, ["result"] = Request.result.ToString(),
                    ["elapsedSeconds"] = Time.realtimeSinceStartup - StartedAt,
                    ["audioSeconds"] = Pcm.AudioDuration, ["bufferSeconds"] = Pcm.BufferedSeconds
                });
            }
            if (!ready && Pcm.HeaderReady && !Pcm.FormatError && Pcm.TotalSamplesReceived > 0 &&
                (Operation.isDone || Pcm.BufferedSeconds >= PrebufferSeconds))
            {
                ready = true;
                ReadyAt = Time.realtimeSinceStartup;
                Trace?.Mark("tts.buffer_ready", new Newtonsoft.Json.Linq.JObject {
                    ["receiveComplete"] = Operation.isDone, ["bufferSeconds"] = Pcm.BufferedSeconds,
                    ["elapsedSeconds"] = Time.realtimeSinceStartup - StartedAt
                });
            }
        }

        public override void Dispose()
        {
            if (Disposed) return;
            Observe();
            Disposed = true;
            if (!Operation.isDone) Request.Abort();
            Trace?.Mark("tts.request_released");
            Request.Dispose();
        }
    }

    private sealed class LatencyFillerEntry
    {
        public readonly string Context;
        public readonly string Text;
        public AudioClip Clip;

        public LatencyFillerEntry(string context, string text)
        {
            Context = context;
            Text = text;
        }
    }

    private sealed class LatencyFillerWarmTarget
    {
        public readonly Language Language;
        public readonly LatencyFillerEntry Entry;

        public LatencyFillerWarmTarget(Language language, LatencyFillerEntry entry)
        {
            Language = language;
            Entry = entry;
        }
    }

    #region 数据定义

    [Serializable]
    public class RequestData
    {
        public string ref_audio_path = string.Empty;
        public string prompt_text = string.Empty;
        public string prompt_lang = string.Empty;
        public string text = string.Empty;
        public string text_lang = string.Empty;
        public int streaming_mode = 0;
        public string media_type = "wav";
        public int min_chunk_length = 16;
    }

    /// <summary>
    /// 将分块 WAV 响应解析为线程安全的 PCM 队列。Unity 音频线程通过
    /// PCMReaderCallback 消费，网络回调则持续生产。
    /// </summary>
    private sealed class PcmStreamingDownloadHandler : DownloadHandlerScript
    {
        public Action OnHeader;
        public Action OnFirstPcm;
        private readonly object m_Gate = new object();
        private readonly List<byte> m_Header = new List<byte>(44);
        private readonly Queue<float[]> m_Blocks = new Queue<float[]>();
        private int m_HeadOffset;
        private int m_BufferedSamples;
        private long m_TotalSamples;
        private long m_TotalOutputSamples;
        private long m_LastRealOutputSamplePosition;
        private long m_InsertedSilenceSamples;
        private int m_PendingUnderrunSignals;
        private long m_FirstPendingUnderrunOutputSample = -1;
        private int m_MaximumCallbackSamples;
        private bool m_InputComplete;
        private bool m_HasOddByte;
        private byte m_OddByte;

        public bool HeaderReady { get; private set; }
        public bool FormatError { get; private set; }
        public string FormatErrorMessage { get; private set; } = string.Empty;
        public int SampleRate { get; private set; } = 32000;
        public int Channels { get; private set; } = 1;
        public int MaximumCallbackSamples { get { lock (m_Gate) return m_MaximumCallbackSamples; } }

        public int BufferedSampleCount
        {
            get { lock (m_Gate) return m_BufferedSamples; }
        }

        public long TotalSamplesReceived
        {
            get { lock (m_Gate) return m_TotalSamples; }
        }

        public long TotalOutputSamplesWritten
        {
            get { lock (m_Gate) return m_TotalOutputSamples; }
        }

        public long LastRealOutputSamplePosition
        {
            get { lock (m_Gate) return m_LastRealOutputSamplePosition; }
        }

        public float InsertedSilenceSeconds
        {
            get
            {
                lock (m_Gate)
                {
                    return SampleRate > 0 && Channels > 0
                        ? m_InsertedSilenceSamples / (float)(SampleRate * Channels)
                        : 0f;
                }
            }
        }

        public float BufferedSeconds => SampleRate > 0 && Channels > 0
            ? BufferedSampleCount / (float)(SampleRate * Channels)
            : 0f;

        public float AudioDuration => SampleRate > 0 && Channels > 0
            ? TotalSamplesReceived / (float)(SampleRate * Channels)
            : 0f;

        public PcmStreamingDownloadHandler() : base(new byte[64 * 1024]) { }

        public void MarkInputComplete()
        {
            lock (m_Gate) m_InputComplete = true;
        }

        // Normal EOF padding must not raise the next turn's adaptive buffer.
        protected override void CompleteContent() => MarkInputComplete();

        public int ConsumeUnderrunSignals()
        {
            return ConsumeUnderrunSnapshot(out _);
        }

        public int ConsumeUnderrunSnapshot(out long firstOutputSample)
        {
            lock (m_Gate)
            {
                int count = m_PendingUnderrunSignals;
                firstOutputSample = m_FirstPendingUnderrunOutputSample;
                m_PendingUnderrunSignals = 0;
                m_FirstPendingUnderrunOutputSample = -1;
                return count;
            }
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;

            bool headerArrived, firstPcmArrived;
            lock (m_Gate)
            {
                bool hadHeader = HeaderReady;
                long previousSamples = m_TotalSamples;
                int offset = 0;
                while (m_Header.Count < 44 && offset < dataLength)
                {
                    m_Header.Add(data[offset++]);
                }

                if (!HeaderReady && m_Header.Count == 44)
                {
                    ParseHeader();
                }

                if (HeaderReady && !FormatError && offset < dataLength)
                {
                    AppendPcm16(data, offset, dataLength - offset);
                }
                headerArrived = !hadHeader && HeaderReady;
                firstPcmArrived = previousSamples == 0 && m_TotalSamples > 0;
            }
            // Trace callbacks may allocate/log: keep them outside the audio lock.
            if (headerArrived) OnHeader?.Invoke();
            if (firstPcmArrived) OnFirstPcm?.Invoke();
            return true;
        }

        private void ParseHeader()
        {
            byte[] h = m_Header.ToArray();
            bool riff = h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F';
            bool wave = h[8] == (byte)'W' && h[9] == (byte)'A' && h[10] == (byte)'V' && h[11] == (byte)'E';
            short format = BitConverter.ToInt16(h, 20);
            short bits = BitConverter.ToInt16(h, 34);
            Channels = BitConverter.ToInt16(h, 22);
            SampleRate = BitConverter.ToInt32(h, 24);
            HeaderReady = true;

            if (!riff || !wave || format != 1 || bits != 16 || Channels <= 0 || SampleRate <= 0)
            {
                FormatError = true;
                FormatErrorMessage = $"unsupported WAV: riff={riff}, wave={wave}, format={format}, bits={bits}, channels={Channels}, rate={SampleRate}";
            }
        }

        private void AppendPcm16(byte[] data, int offset, int count)
        {
            int availableBytes = count + (m_HasOddByte ? 1 : 0);
            int sampleCount = availableBytes / 2;
            if (sampleCount <= 0)
            {
                if (count == 1)
                {
                    m_OddByte = data[offset];
                    m_HasOddByte = true;
                }
                return;
            }

            float[] samples = new float[sampleCount];
            int sampleIndex = 0;
            int end = offset + count;

            if (m_HasOddByte && offset < end)
            {
                short value = (short)(m_OddByte | (data[offset++] << 8));
                samples[sampleIndex++] = value / 32768f;
                m_HasOddByte = false;
            }

            while (offset + 1 < end)
            {
                short value = (short)(data[offset] | (data[offset + 1] << 8));
                samples[sampleIndex++] = value / 32768f;
                offset += 2;
            }

            if (offset < end)
            {
                m_OddByte = data[offset];
                m_HasOddByte = true;
            }

            if (sampleIndex != samples.Length)
            {
                Array.Resize(ref samples, sampleIndex);
            }
            if (samples.Length > 0)
            {
                m_Blocks.Enqueue(samples);
                m_BufferedSamples += samples.Length;
                m_TotalSamples += samples.Length;
            }
        }

        public void ReadSamples(float[] output)
        {
            int written = 0;
            lock (m_Gate)
            {
                m_MaximumCallbackSamples = Math.Max(m_MaximumCallbackSamples, output.Length);
                // Consume available PCM in order and pad only the shortage.
                // Holding partial audio used to insert an entire silent callback.
                while (written < output.Length && m_Blocks.Count > 0)
                {
                    float[] block = m_Blocks.Peek();
                    int copy = Mathf.Min(output.Length - written, block.Length - m_HeadOffset);
                    Array.Copy(block, m_HeadOffset, output, written, copy);
                    written += copy;
                    m_HeadOffset += copy;
                    m_BufferedSamples -= copy;

                    if (m_HeadOffset >= block.Length)
                    {
                        m_Blocks.Dequeue();
                        m_HeadOffset = 0;
                    }
                }

                //包括本回调尾部填入的静音；它代表 Unity DSP 时间线上已经排队的总采样数。
                //最后一批真实 PCM 消费完时，协程会快照这个位置作为安全停止点。
                m_TotalOutputSamples += output.Length;
                if (written > 0)
                {
                    m_LastRealOutputSamplePosition = m_TotalOutputSamples - (output.Length - written);
                }
                if (!m_InputComplete && written < output.Length)
                {
                    if (m_FirstPendingUnderrunOutputSample < 0)
                        m_FirstPendingUnderrunOutputSample = m_TotalOutputSamples - output.Length + written;
                    m_InsertedSilenceSamples += output.Length - written;
                    ++m_PendingUnderrunSignals;
                }
            }

            if (written < output.Length)
            {
                Array.Clear(output, written, output.Length - written);
            }
        }
    }

    public enum Language
    {
        中文,
        英文,
        日文
    }

    public enum TargetLanguageMode
    {
        自动识别,
        固定语言
    }

    /// <summary>
    /// 供UI、Agent工具或其他Unity组件安全切换目标语言。
    /// 仅接受 auto / zh / ja / en 及常见别名，不开放任意参数写入。
    /// </summary>
    public bool TrySetTargetLanguage(string language)
    {
        string normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "auto":
            case "自动":
            case "自动识别":
                m_TargetLanguageMode = TargetLanguageMode.自动识别;
                return true;
            case "zh":
            case "zh-cn":
            case "chinese":
            case "中文":
                SetTargetLanguage(Language.中文);
                return true;
            case "en":
            case "en-us":
            case "english":
            case "英文":
            case "英语":
                SetTargetLanguage(Language.英文);
                return true;
            case "ja":
            case "ja-jp":
            case "jp":
            case "japanese":
            case "日文":
            case "日语":
                SetTargetLanguage(Language.日文);
                return true;
            default:
                Debug.LogWarning($"[TTS语言] 忽略不支持的语言值: {language}");
                return false;
        }
    }

    public void SetTargetLanguage(Language language)
    {
        m_TargetTextLan = language;
        m_TargetLanguageMode = TargetLanguageMode.固定语言;
        if (m_LogLanguageChanges)
        {
            Debug.Log($"[TTS语言] 已固定为 {ConvertLanguageEnum(language)}");
        }
    }

    public void EnableAutoTargetLanguage()
    {
        m_TargetLanguageMode = TargetLanguageMode.自动识别;
        if (m_LogLanguageChanges) Debug.Log("[TTS语言] 已切换为自动识别");
    }

    public string CurrentTargetLanguageCode =>
        m_TargetLanguageMode == TargetLanguageMode.自动识别
            ? "auto"
            : ConvertLanguageEnum(m_TargetTextLan);

    public override bool TryPlayLatencyFiller(
        string languageHint,
        string contextHint,
        AudioSource output,
        out string spokenText,
        out float duration)
    {
        spokenText = string.Empty;
        duration = 0f;
        if (output == null) return false;

        Language predictedLanguage = PredictTargetLanguage(languageHint);
        EnsureLatencyFillerEntries(predictedLanguage);
        List<LatencyFillerEntry> entries = m_LatencyFillerClips[predictedLanguage];
        string context = NormalizeLatencyFillerContext(contextHint);
        List<LatencyFillerEntry> candidates = GetReadyLatencyFillers(entries, context);
        if (candidates.Count == 0 && context != "neutral")
            candidates = GetReadyLatencyFillers(entries, "neutral");
        if (candidates.Count == 0)
        {
            foreach (LatencyFillerEntry entry in entries)
            {
                if (entry.Clip != null) candidates.Add(entry);
            }
        }
        if (candidates.Count == 0) return false;

        string cursorKey = ConvertLanguageEnum(predictedLanguage) + ":" + context;
        int cursor;
        if (!m_LatencyFillerCursors.TryGetValue(cursorKey, out cursor)) cursor = 0;
        cursor = Mathf.Abs(cursor) % candidates.Count;

        string lastText;
        m_LastLatencyFillerTexts.TryGetValue(predictedLanguage, out lastText);
        int selectedIndex = cursor;
        if (candidates.Count > 1 && candidates[selectedIndex].Text == lastText)
            selectedIndex = (selectedIndex + 1) % candidates.Count;

        LatencyFillerEntry selected = candidates[selectedIndex];
        m_LatencyFillerCursors[cursorKey] = (selectedIndex + 1) % candidates.Count;
        m_LastLatencyFillerTexts[predictedLanguage] = selected.Text;

        spokenText = selected.Text;
        duration = selected.Clip.length;
        output.clip = selected.Clip;
        output.loop = false;
        output.Play();
        return true;
    }

    private static List<LatencyFillerEntry> GetReadyLatencyFillers(
        List<LatencyFillerEntry> entries, string context)
    {
        var result = new List<LatencyFillerEntry>();
        foreach (LatencyFillerEntry entry in entries)
        {
            if (entry.Clip != null && entry.Context == context) result.Add(entry);
        }
        return result;
    }

    private static string NormalizeLatencyFillerContext(string contextHint)
    {
        if (string.Equals(contextHint, "singing", StringComparison.OrdinalIgnoreCase)) return "singing";
        if (string.Equals(contextHint, "thinking", StringComparison.OrdinalIgnoreCase)) return "thinking";
        return "neutral";
    }

    private Language PredictTargetLanguage(string hint)
    {
        if (m_TargetLanguageMode == TargetLanguageMode.固定语言) return m_TargetTextLan;

        string text = hint ?? string.Empty;
        if (ContainsAny(text, "英语", "英文", "English", "英語")) return Language.英文;
        if (ContainsAny(text, "日语", "日文", "日本語", "Japanese")) return Language.日文;
        if (ContainsAny(text, "中文", "汉语", "漢語", "普通话", "Chinese")) return Language.中文;
        return DetectLanguage(text, m_TargetTextLan);
    }

    private static bool ContainsAny(string text, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private Language ResolveTargetLanguage(string text)
    {
        if (m_TargetLanguageMode == TargetLanguageMode.固定语言)
        {
            return m_TargetTextLan;
        }

        // m_TargetTextLan is the Inspector-configured fallback, not a conversation
        // state. Mutating it here made an untyped Han-only utterance in one request
        // change the fallback used by the next request ("了解" could alternate
        // between zh and ja depending on what ran immediately before it).
        Language fallback = m_TargetTextLan;
        Language detected = DetectLanguage(text, fallback);
        if (m_LogLanguageChanges && detected != fallback)
        {
            Debug.Log($"[TTS语言] 自动识别 fallback={ConvertLanguageEnum(fallback)} -> {ConvertLanguageEnum(detected)}");
        }
        return detected;
    }

    private RequestData CreateSpeechRequest(string text, string languageCode, int streamingMode)
    {
        return new RequestData
        {
            ref_audio_path = ResolveReferenceAudioPath(),
            prompt_text = m_ReferenceText,
            prompt_lang = ConvertLanguageEnum(m_ReferenceTextLan),
            text = text,
            text_lang = languageCode,
            streaming_mode = streamingMode,
            media_type = "wav",
            min_chunk_length = m_MinStreamingChunkLength
        };
    }

    public override SpeechText ResolveSpeech(SpeechText speech)
    {
        // Legacy callers may include private analysis; detect only the spoken text.
        speech = new SpeechText(Regex.Replace(speech.Text ?? string.Empty,
            "<think>.*?</think>", "", RegexOptions.Singleline).Trim(),
            speech.LanguageCode, speech.LanguageSource, speech.ActionDependency, speech.SuppressTts);
        string source;
        string code;
        if (m_TargetLanguageMode == TargetLanguageMode.固定语言)
        {
            source = "fixed";
            code = ConvertLanguageEnum(m_TargetTextLan);
        }
        else if (speech.LanguageCode != null)
        {
            // A queued or prefetched utterance must not change another request's language.
            source = speech.LanguageSource ?? "declared";
            code = speech.LanguageCode;

            // Prefetch and playback both call this method. An earlier correction must
            // not be treated as a new model declaration.
            string correctedCode = string.Equals(source, "declared", StringComparison.Ordinal)
                ? ResolveDeclaredLanguageConflict(speech.Text, code) : code;
            if (!string.Equals(correctedCode, code, StringComparison.Ordinal))
            {
                Debug.LogWarning($"[TTS语言] 声明与正文脚本冲突，按强证据修正: declared={code} -> {correctedCode} text=\"{speech.Text}\"");
                source = "declared-conflict-auto";
                code = correctedCode;
            }
        }
        else
        {
            source = "auto-fallback";
            code = ConvertLanguageEnum(ResolveTargetLanguage(speech.Text));
        }
        if (m_LogLanguageChanges)
            Debug.Log($"[TTS语言] source={source} lang={code} text=\"{speech.Text}\"");
        return new SpeechText(speech.Text, code, source, speech.ActionDependency, speech.SuppressTts);
    }

    private static string ResolveDeclaredLanguageConflict(string text, string declaredCode)
    {
        CountCjkScripts(text, out int kanaCount, out int hanCount);
        bool hasStrongChinese = ContainsStrongSimplifiedChineseCharacter(text);

        // Explicit language wins for ambiguous mixed text. In particular, a Japanese
        // suffix must not turn a declared Chinese quotation into Japanese audio. Keep
        // correcting pure kana and clearly kana-dominant Japanese mislabeled as zh.
        if (string.Equals(declaredCode, "zh", StringComparison.Ordinal) && !hasStrongChinese &&
            kanaCount > 0 && (hanCount == 0 || (kanaCount >= 2 && kanaCount >= hanCount)) &&
            !ContainsHanQuotationWithoutKana(text))
            return "ja";

        // A Chinese quotation inside Japanese does not change the enclosing language.
        // This rule and the kana rule must not contradict each other on a mixed span.
        if (string.Equals(declaredCode, "ja", StringComparison.Ordinal) &&
            hasStrongChinese && kanaCount == 0)
            return "zh";

        return declaredCode;
    }

    private static bool ContainsHanQuotationWithoutKana(string text)
    {
        // A short declared Chinese quote (e.g. 「了解」です。) remains ambiguous even
        // when the surrounding Japanese has more kana than the quote has Han letters.
        // Japanese-declared quotes are unaffected; the declaration supplies intent.
        const string quotedText = @"「(?<quote>[^」]+)」|『(?<quote>[^』]+)』|“(?<quote>[^”]+)”|""(?<quote>[^""]+)""";
        foreach (Match match in Regex.Matches(text ?? string.Empty, quotedText))
        {
            CountCjkScripts(match.Groups["quote"].Value, out int kanaCount, out int hanCount);
            if (hanCount > 0 && kanaCount == 0) return true;
        }
        return false;
    }

    private static void CountCjkScripts(string text, out int kanaCount, out int hanCount)
    {
        kanaCount = 0;
        hanCount = 0;
        foreach (char c in text ?? string.Empty)
        {
            if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\uff66' && c <= '\uff9d'))
                kanaCount++;
            else if (c >= '\u4e00' && c <= '\u9fff')
                hanCount++;
        }
    }

    private static bool ContainsStrongSimplifiedChineseCharacter(string text)
    {
        const string strongChineseOnly = "们这么说听让还过见为实现样时问间";
        foreach (char c in text ?? string.Empty)
        {
            if (strongChineseOnly.IndexOf(c) >= 0) return true;
        }
        return false;
    }

    private static Language DetectLanguage(string text, Language fallback)
    {
        int kanaCount = 0;
        int hanCount = 0;
        int latinCount = 0;
        int latinWordCount = 0;
        bool insideLatinWord = false;

        foreach (char c in text ?? string.Empty)
        {
            if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\uff66' && c <= '\uff9d'))
            {
                kanaCount++;
                insideLatinWord = false;
            }
            else if (c >= '\u4e00' && c <= '\u9fff')
            {
                hanCount++;
                insideLatinWord = false;
            }
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                latinCount++;
                if (!insideLatinWord) latinWordCount++;
                insideLatinWord = true;
            }
            else
            {
                insideLatinWord = false;
            }
        }

        //只要含有假名，就按日文处理；日文中的汉字不应被误判成中文。
        if (kanaCount > 0) return Language.日文;
        //中英混合时按“英文词数 vs 汉字数”判断主语言，避免长英文单词仅因字母多而压过中文正文。
        if (hanCount > 0) return latinWordCount > hanCount ? Language.英文 : Language.中文;
        if (latinCount > 0) return Language.英文;
        return fallback;
    }

    private string ConvertLanguageEnum(Language lang)
    {
        switch (lang)
        {
            case Language.中文: return "zh";
            case Language.英文: return "en";
            case Language.日文: return "ja";
            default: return "zh";
        }
    }

    /// <summary>
    /// Project-local reference audio is sent as an absolute path because the
    /// GPT-SoVITS server may have a different working directory from Unity.
    /// If no matching Unity asset exists, keep the original server-side path.
    /// </summary>
    /// <summary>
    /// Exposes the exact same clean character reference used by dialogue TTS to the local
    /// singing voice-conversion bridge.  This avoids maintaining a second voice setting.
    /// </summary>
    public string GetReferenceAudioPathForVoiceConversion()
    {
        if (string.IsNullOrWhiteSpace(m_VoiceConversionReferencePath))
            return ResolveReferenceAudioPath();
        return ResolveConfiguredAudioPath(m_VoiceConversionReferencePath);
    }

    private string ResolveReferenceAudioPath()
    {
        return ResolveConfiguredAudioPath(m_ReferWavPath);
    }

    private static string ResolveConfiguredAudioPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath.Replace('\\', '/');
        }

        string assetPath = Path.GetFullPath(Path.Combine(Application.dataPath, configuredPath));
        if (File.Exists(assetPath))
        {
            return assetPath.Replace('\\', '/');
        }

        return configuredPath.Replace('\\', '/');
    }

    #endregion
}
