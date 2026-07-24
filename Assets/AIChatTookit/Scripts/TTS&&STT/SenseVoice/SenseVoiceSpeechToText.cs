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
    [SerializeField] private bool m_AutoBindIntroducedName = true;

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
    private string m_LastSingingLyrics = "";
    private float m_LastSingingAudioTime = -999f;
    private float m_LastSingingPerformanceTime = -999f;
    private float[] m_LastSingingPerformanceMidi = new float[0];
    private float m_LastSingingPerformanceFrameSeconds = 0.10f;
    private string m_LastSingingPerformanceLanguage = "";
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
                        audioCropSeconds = Mathf.Min(
                            acousticAudioCropSeconds, protectedStreamingCropSeconds);
                    }
                    else if (expectSinging && LastIsSinging)
                    {
                        // An expected sing-along is allowed to be conservative.  The offline
                        // longest-island detector can jump to the second phrase when streaming
                        // did not reach its stable-singing gate (the July 22 log cut 9.83 s this
                        // way).  With no independent onset anchor, preserving the complete take
                        // is safer than silently deleting a real opening; a short breath or
                        // spoken lead-in is an acceptable trade-off.
                        audioCropSeconds = 0f;
                    }
                    float croppedContentSeconds = Mathf.Max(
                        0f, audioCropSeconds - _response.audio_content_start_seconds);
                    float timelineCropSeconds = Mathf.Max(
                        0f, croppedContentSeconds - _response.pitch_timeline_start_seconds);
                    // The tail text is authoritative even when no explicit sing-along request
                    // was armed. Otherwise a spontaneous sung phrase followed by "不会唱了"
                    // could still overwrite the last clean performance cache.
                    bool endsWithSpokenSingingExit = streamingSpokenExitDetected ||
                        EndsWithSpokenSingingExit(LastText);

                    if (expectSinging || m_VerboseLog)
                    {
                        Debug.Log($"[SenseVoice/Singing] final expected={expectSinging} " +
                                  $"singing={LastIsSinging} prob={LastSingingProbability:F2} " +
                                  $"stability={LastPitchStability:F2} voiced={_response.voiced_ratio:F2} " +
                                  $"sustained={_response.sustained_ratio:F2} " +
                                  $"timeline={LastPitchTimelineMidi.Length} " +
                                  $"spokenExit={endsWithSpokenSingingExit} " +
                                  $"streamExitHint={streamingSpokenExitDetected}");
                        Debug.Log($"[SenseVoice/Singing] crop raw={rawAudioSeconds:F2}s " +
                                  $"acoustic={acousticAudioCropSeconds:F2}s " +
                                  $"streamOnset={streamingSingingOnsetSeconds:F2}s " +
                                  $"streamObserved={streamingObservedSeconds:F2}s " +
                                  $"protected={protectedStreamingCropSeconds:F2}s " +
                                  $"applied={audioCropSeconds:F2}s " +
                                  $"timeline={timelineCropSeconds:F2}s");
                    }

                    bool hasPlayablePitch = HasPlayablePitchTimeline(LastPitchTimelineMidi);
                    if (hasPlayablePitch && !endsWithSpokenSingingExit)
                    {
                        m_LastPlayableCandidateAudioBytes = audioBytes;
                        m_LastPlayableCandidateTime = Time.realtimeSinceStartup;
                        m_LastPlayableCandidateAudioCropSeconds = audioCropSeconds;
                        m_LastPlayableCandidateTimelineCropSeconds = timelineCropSeconds;
                    }

                    if (LastIsSinging && !endsWithSpokenSingingExit)
                    {
                        CacheLastSingingPerformance(
                            audioBytes,
                            audioCropSeconds,
                            timelineCropSeconds);
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
               "歌词是ASR推测，长音与一字多音处可能不准确] ";
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
        bool strongStreamingEvidence = streamingProbability >= 0.55f ||
            streamingPitchStability >= 0.52f;
        bool freshCandidate = Time.realtimeSinceStartup - m_LastPlayableCandidateTime <= 5f &&
            m_LastPlayableCandidateAudioBytes != null &&
            m_LastPlayableCandidateAudioBytes.Length > 44;
        if (!strongStreamingEvidence || !freshCandidate ||
            !HasPlayablePitchTimeline(LastPitchTimelineMidi))
            return false;

        LastIsSinging = true;
        LastSingingProbability = Mathf.Max(LastSingingProbability, streamingProbability);
        LastPitchStability = Mathf.Max(LastPitchStability, streamingPitchStability);
        CacheLastSingingPerformance(
            m_LastPlayableCandidateAudioBytes,
            m_LastPlayableCandidateAudioCropSeconds,
            m_LastPlayableCandidateTimelineCropSeconds);
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
            if (index >= tailStart) return true;
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

    private void CacheLastSingingPerformance(
        byte[] audioBytes,
        float audioCropSeconds = 0f,
        float timelineCropSeconds = 0f)
    {
        m_RollbackSingingAudioBytes = m_LastSingingAudioBytes;
        m_RollbackSingingLyrics = m_LastSingingLyrics;
        m_RollbackSingingAudioTime = m_LastSingingAudioTime;
        m_RollbackSingingPerformanceTime = m_LastSingingPerformanceTime;
        m_RollbackSingingPerformanceMidi = m_LastSingingPerformanceMidi;
        m_RollbackSingingPerformanceFrameSeconds =
            m_LastSingingPerformanceFrameSeconds;
        m_RollbackSingingPerformanceLanguage =
            m_LastSingingPerformanceLanguage;
        m_LastSingingCacheSerial = m_LastCompletedAsrSerial;

        float now = Time.realtimeSinceStartup;
        m_LastSingingLyrics = LastText ?? "";
        float actualAudioCrop = 0f;
        if (audioBytes != null && audioBytes.Length > 44)
        {
            m_LastSingingAudioBytes = TrimWavLeading(
                audioBytes,
                audioCropSeconds,
                out actualAudioCrop);
            m_LastSingingAudioTime = now;
        }
        if (!HasPlayablePitchTimeline(LastPitchTimelineMidi)) return;

        int timelineStart = Mathf.Clamp(
            Mathf.FloorToInt(timelineCropSeconds / Mathf.Max(0.02f, LastPitchTimelineFrameSeconds)),
            0,
            Mathf.Max(0, LastPitchTimelineMidi.Length - 1));
        int timelineLength = LastPitchTimelineMidi.Length - timelineStart;
        m_LastSingingPerformanceMidi = new float[timelineLength];
        Array.Copy(
            LastPitchTimelineMidi,
            timelineStart,
            m_LastSingingPerformanceMidi,
            0,
            timelineLength);
        if (!HasPlayablePitchTimeline(m_LastSingingPerformanceMidi))
        {
            m_LastSingingPerformanceMidi = new float[LastPitchTimelineMidi.Length];
            Array.Copy(
                LastPitchTimelineMidi,
                m_LastSingingPerformanceMidi,
                m_LastSingingPerformanceMidi.Length);
            timelineStart = 0;
        }
        m_LastSingingPerformanceFrameSeconds = LastPitchTimelineFrameSeconds;
        m_LastSingingPerformanceLanguage = LastLanguage ?? "";
        m_LastSingingPerformanceTime = now;
        if (actualAudioCrop >= 0.05f || timelineStart > 0)
        {
            Debug.Log($"[SenseVoice/Singing] cached performance after preface trim " +
                      $"audio={actualAudioCrop:F2}s timeline={timelineStart * LastPitchTimelineFrameSeconds:F2}s " +
                      $"frames={m_LastSingingPerformanceMidi.Length}");
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

    private static byte[] TrimWavLeading(
        byte[] wavBytes,
        float startSeconds,
        out float actualStartSeconds)
    {
        actualStartSeconds = 0f;
        if (wavBytes == null || wavBytes.Length <= 44 || startSeconds < 0.05f)
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
        if (skipBytes <= 0 || remaining < Mathf.CeilToInt(byteRate * 0.35f))
            return wavBytes;

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

    public int PracticePhraseCount
    {
        get { return m_PracticePhrases.Count; }
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
    public bool CommitRecentSingingToPracticeSession(out int phraseCount)
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

        if (m_PracticePhrases.Count >= MaxPracticePhraseCount)
            m_PracticePhrases.RemoveAt(0);
        m_PracticePhrases.Add(new PracticePhrase
        {
            WavBytes = wavBytes,
            MidiTimeline = timeline,
            FrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f),
            Language = language ?? "",
            Signature = signature,
        });
        m_LastCommittedPracticeSignature = signature;
        m_LastPracticeCommitTime = Time.realtimeSinceStartup;
        phraseCount = m_PracticePhrases.Count;
        Debug.Log($"[SenseVoice/Practice] 最终歌声已提交 sequence={phraseCount} " +
                  $"audio={GetWavDurationSeconds(wavBytes):F2}s frames={timeline.Length}");
        return true;
    }

    /// <summary>
    /// Creates a private performance variant of the latest phrase. The melody is not
    /// rewritten: only sub-percent pacing and a slow dynamics contour change between takes.
    /// </summary>
    public bool TryGetVariedRecentSingingAudio(
        int performanceSeed,
        out byte[] wavBytes,
        out string diagnostic)
    {
        wavBytes = null;
        diagnostic = "";
        if (!TryGetRecentSingingAudio(out byte[] source) ||
            !TryDecodePcmWav(source, out float[] samples, out int sampleRate))
            return false;

        System.Random random = new System.Random(performanceSeed);
        float pace = 0.994f + (float)random.NextDouble() * 0.012f;
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
    public bool TryBuildSingingPracticeComposition(
        int performanceSeed,
        float maxSeconds,
        out PracticeComposition composition,
        out string failure)
    {
        composition = null;
        failure = "";
        if (m_PracticePhrases.Count < 2)
        {
            failure = $"练唱会话只有 {m_PracticePhrases.Count} 段，至少需要两段才能连续演唱";
            return false;
        }

        var decoded = new List<float[]>(m_PracticePhrases.Count);
        int outputRate = 0;
        for (int i = 0; i < m_PracticePhrases.Count; i++)
        {
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
            }

            output.AddRange(phrase);
            AppendResampledTimeline(
                midi,
                m_PracticePhrases[i].MidiTimeline,
                m_PracticePhrases[i].FrameSeconds,
                outputFrameSeconds,
                pace);
            if (string.IsNullOrEmpty(language) &&
                !string.IsNullOrEmpty(m_PracticePhrases[i].Language))
                language = m_PracticePhrases[i].Language;
            variation.Append($" p{i + 1}={pace:F3}/{gainStart:F2}->{gainEnd:F2}");
        }

        float duration = output.Count / (float)Mathf.Max(1, outputRate);
        if (duration > maxSeconds + 0.02f)
        {
            failure = $"连续演唱需要 {duration:F1}s，超过当前完整转换上限 {maxSeconds:F1}s；没有裁掉开头";
            return false;
        }
        float[] outputSamples = output.ToArray();
        ApplyShortEdgeFade(outputSamples, outputRate, 0.018f);
        composition = new PracticeComposition
        {
            WavBytes = EncodeMonoPcm16Wav(outputSamples, outputRate),
            MidiTimeline = midi.ToArray(),
            FrameSeconds = outputFrameSeconds,
            Language = language,
            PhraseCount = m_PracticePhrases.Count,
            DurationSeconds = duration,
            VariationDiagnostic = $"seed={performanceSeed};{variation.ToString().Trim()}",
        };
        return composition.WavBytes != null && composition.WavBytes.Length > 44 &&
            HasPlayablePitchTimeline(composition.MidiTimeline);
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
        Action<SongPerformanceResult> callback)
    {
        StartCoroutine(SendRememberedSongPerformance(
            songId, title, mode, maxSeconds, seed, reason, callback));
    }

    private IEnumerator SendRememberedSongPerformance(
        string songId,
        string title,
        string mode,
        float maxSeconds,
        int seed,
        string reason,
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
            string name = match.Groups["name"].Value.Trim();
            if (name.Length > 0 && name.Length <= 32) return name;
        }
        return "";
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
        public float pitch_timeline_start_seconds = 0f;
        public float audio_content_start_seconds = 0f;
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
