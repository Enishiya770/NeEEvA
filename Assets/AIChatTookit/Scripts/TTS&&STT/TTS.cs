using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class TTS : MonoBehaviour
{
    /// <summary>
    /// �����ϳɵ�api��ַ
    /// </summary>
    [SerializeField] protected string m_PostURL = string.Empty;
    /// <summary>
    /// ���㷽�����õ�ʱ��
    /// </summary>
    [SerializeField] protected Stopwatch stopwatch = new Stopwatch();
    /// <summary>
    /// �����ϳɣ�������Ƶ
    /// </summary>
    /// <param name="_msg"></param>
    /// <param name="_callback"></param>
    public virtual void Speak(string _msg,Action<AudioClip> _callback) {}
    /// <summary>
    /// �ϳ�����������Ƶ��ͬʱ���غϳɵ��ı�
    /// </summary>
    /// <param name="_msg"></param>
    /// <param name="_callback"></param>
    public virtual void Speak(string _msg, Action<AudioClip,string> _callback) { }

    /// <summary>
    /// 预热TTS服务：发一条极短的合成请求触发后端加载模型到显存，丢弃结果。
    /// 消除首次真实请求的冷启动延迟。子类按需实现。
    /// </summary>
    public virtual void WarmUp() { }

    /// <summary>
    /// 是否支持边接收 PCM 边直接写入指定 AudioSource。
    /// 默认 TTS 仍走完整 AudioClip 回调，只有实现了流式播放的子类返回 true。
    /// </summary>
    public virtual bool SupportsStreamingPlayback => false;

    /// <summary>
    /// 流式合成并直接播放。onStarted 在首批 PCM 开始播放时触发，
    /// onCompleted(success, text, audioDuration) 在音频播放完或失败时触发。
    /// </summary>
    public virtual void SpeakStreaming(
        string text,
        AudioSource output,
        Action<string> onStarted,
        Action<bool, string, float> onCompleted)
    {
        onCompleted?.Invoke(false, text, 0f);
    }

    /// <summary>取消当前流式请求与播放。</summary>
    public virtual void CancelStreaming() { }

    /// <summary>真实对话即将开始；子类可中止后台预热，把推理资源让给正式请求。</summary>
    public virtual void PrioritizeConversation() { }

    /// <summary>
    /// 在用户仍在说话/唱歌时静默预合成一句可撤销的短开场。结果只返回 AudioClip，
    /// 不会自动播放；正式对话开始时可通过 CancelPreparedSpeech 让出推理资源。
    /// </summary>
    public virtual void PrepareSpeech(string text, Action<AudioClip, string> callback)
    {
        Speak(text, callback);
    }

    /// <summary>取消尚未完成的静默预合成请求。</summary>
    public virtual void CancelPreparedSpeech() { }

    /// <summary>
    /// 播放启动时预缓存的短回应，不发起新的TTS请求。
    /// languageHint用于推测回应语言；返回false表示当前TTS没有可用缓存。
    /// </summary>
    public virtual bool TryPlayLatencyFiller(
        string languageHint,
        AudioSource output,
        out string spokenText,
        out float duration)
    {
        return TryPlayLatencyFiller(
            languageHint, "neutral", output, out spokenText, out duration);
    }

    /// <summary>
    /// 播放与当前场景匹配的预缓存短回应。contextHint 可为 neutral、thinking 或 singing。
    /// </summary>
    public virtual bool TryPlayLatencyFiller(
        string languageHint,
        string contextHint,
        AudioSource output,
        out string spokenText,
        out float duration)
    {
        spokenText = string.Empty;
        duration = 0f;
        return false;
    }

}
