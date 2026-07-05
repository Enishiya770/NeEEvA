using System;
using System.Collections;
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

    [Header("识别语言: auto / zh / en / ja / ko / yue")]
    [SerializeField] private string m_Language = "auto";

    [Header("把 emotion / event 注入回调文本前缀 —— 例: [情绪:SAD 事件:Laughter] 你好")]
    [SerializeField] private bool m_InjectMetaPrefix = true;

    [Header("跳过这些默认/无意义事件，不注入前缀")]
    [SerializeField] private string[] m_SkipEvents = new string[] { "Speech", "BGM" };

    [Header("跳过这些默认/无意义情绪，不注入前缀")]
    [SerializeField] private string[] m_SkipEmotions = new string[] { "NEUTRAL" };

    [Header("输出详细日志")]
    [SerializeField] private bool m_VerboseLog = true;

    #endregion

    #region 外部可读的最近一次识别结果 (主业务若想单独取用情绪/事件)

    public string LastText { get; private set; } = "";
    public string LastEmotion { get; private set; } = "";
    public string LastEvent { get; private set; } = "";
    public string LastLanguage { get; private set; } = "";

    #endregion

    private void Awake()
    {
        m_SpeechRecognizeURL = m_ServerSetting.TrimEnd('/') + "/asr";
    }

    public override void SpeechToText(AudioClip _clip, Action<string> _callback)
    {
        byte[] _audioData = WavUtility.FromAudioClip(_clip);
        StartCoroutine(SendAudioData(_audioData, _callback));
    }

    public override void SpeechToText(byte[] _audioData, Action<string> _callback)
    {
        StartCoroutine(SendAudioData(_audioData, _callback));
    }

    private IEnumerator SendAudioData(byte[] audioBytes, Action<string> _callback)
    {
        stopwatch.Restart();

        WWWForm form = new WWWForm();
        form.AddBinaryData("audio_file", audioBytes, "input.wav", "audio/wav");
        form.AddField("language", m_Language);

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
                    LastText = _response.text ?? "";
                    LastLanguage = _response.language ?? "";
                    LastEmotion = _response.emotion ?? "";
                    LastEvent = _response.audio_event ?? "";

                    string finalText = m_InjectMetaPrefix
                        ? (BuildMetaPrefix() + LastText)
                        : LastText;

                    if (m_VerboseLog)
                    {
                        Debug.Log($"[SenseVoice] text=\"{LastText}\" lang={LastLanguage} " +
                                  $"emo={LastEmotion} evt={LastEvent} " +
                                  $"dt={_response.elapsed:F2}s");
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
        public float elapsed = 0f;
    }

    #endregion
}
