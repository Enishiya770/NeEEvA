using Newtonsoft.Json;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using static GPTSoVITSTextToSpeech;
using System.Text.RegularExpressions;

public class GPTSoVITSFASTAPI : TTS
{
    #region 参数定义
    [Header("参考音频路径，是GPT-SoVITS项目下的相对路径")]
    [SerializeField] private string m_ReferWavPath = string.Empty; // 例如: "archive_jingyuan_1.wav"
    
    [Header("参考音频的文字内容")]
    [SerializeField] private string m_ReferenceText = ""; // 示例: "我是景元"

    [Header("参考音频的语言")]
    [SerializeField] private Language m_ReferenceTextLan = Language.中文; // "zh"

    [Header("合成音频的语言")]
    [SerializeField] private Language m_TargetTextLan = Language.中文; // "zh"

    [Header("TTS服务地址")]
    [SerializeField] private string m_PostURL = "http://127.0.0.1:9880/tts";
    #endregion

    public override void Speak(string _msg, Action<AudioClip, string> _callback)
    {
        StartCoroutine(GetVoice(_msg, _callback));
    }

    /// <summary>
    /// 预热：发一条极短的合成请求，触发GPT-SoVITS加载模型到显存。
    /// 之后真实用户请求不会再碰到冷启动(首次合成多花2-4秒)的问题。
    /// 不会向外抛回调，也不会产生任何角色可见的语音。
    /// </summary>
    public override void WarmUp()
    {
        if (string.IsNullOrEmpty(m_ReferWavPath))
        {
            Debug.LogWarning("[TTS预热] m_ReferWavPath未配置，跳过");
            return;
        }
        StartCoroutine(DoWarmUp());
    }

    private IEnumerator DoWarmUp()
    {
        //根据目标语言选一个极短的合法字符
        string warmUpText;
        switch (m_TargetTextLan)
        {
            case Language.英文: warmUpText = "a"; break;
            case Language.日文: warmUpText = "あ"; break;
            case Language.中文:
            default: warmUpText = "嗯"; break;
        }

        RequestData _requestData = new RequestData
        {
            ref_audio_path = m_ReferWavPath,
            prompt_text = m_ReferenceText,
            prompt_lang = ConvertLanguageEnum(m_ReferenceTextLan),
            text = warmUpText,
            text_lang = ConvertLanguageEnum(m_TargetTextLan)
        };
        string _postJson = JsonUtility.ToJson(_requestData);

        float t0 = Time.realtimeSinceStartup;
        using (UnityWebRequest request = new UnityWebRequest(m_PostURL, "POST"))
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(_postJson);
            request.uploadHandler = new UploadHandlerRaw(data);
            request.downloadHandler = new DownloadHandlerAudioClip(m_PostURL, AudioType.WAV);
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            float dt = Time.realtimeSinceStartup - t0;
            if (request.responseCode == 200)
            {
                Debug.Log($"[TTS预热] 完成，耗时 {dt:F2}s");
            }
            else
            {
                Debug.LogWarning($"[TTS预热] 失败(code={request.responseCode}): {request.error}");
            }
        }
    }

    private IEnumerator GetVoice(string _msg, Action<AudioClip, string> _callback)
    {
        stopwatch.Restart();

    // ✅ 在这里清除 <think> 标签
    _msg = Regex.Replace(_msg, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();

        RequestData _requestData = new RequestData
        {
            ref_audio_path = m_ReferWavPath,
            prompt_text = m_ReferenceText,
            prompt_lang = ConvertLanguageEnum(m_ReferenceTextLan),
            text = _msg,
            text_lang = ConvertLanguageEnum(m_TargetTextLan)
        };

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

    #region 数据定义

    [Serializable]
    public class RequestData
    {
        public string ref_audio_path = string.Empty;
        public string prompt_text = string.Empty;
        public string prompt_lang = string.Empty;
        public string text = string.Empty;
        public string text_lang = string.Empty;
    }

    public enum Language
    {
        中文,
        英文,
        日文
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

    #endregion
}
