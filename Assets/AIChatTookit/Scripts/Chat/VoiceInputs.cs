using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoiceInputs : MonoBehaviour
{
    private readonly PersonalSingingLearningRecorder personalLearningRecorder = new PersonalSingingLearningRecorder();

    /// <summary>
    /// 录制缓冲区长度(秒)。Microphone.Start用loop=false时这是硬上限，
    /// 超过此长度的录音会被静音填充。设大点保证用户能从容把话讲完。
    /// </summary>
    public int m_RecordingLength = 60;

    public AudioClip recording;
    private int pitchCursor;
    private float nextPitchRead;
    private bool pitchRecording;

    private void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (!pitchRecording || recording == null || Time.unscaledTime < nextPitchRead) return;
        nextPitchRead = Time.unscaledTime + .2f;
        PumpPitch(Microphone.GetPosition(null));
#endif
    }

    private void PumpPitch(int position)
    {
        if (recording == null || position <= pitchCursor || position > recording.samples) return;
        if (!personalLearningRecorder.WantsPitch) { pitchCursor = position; return; }
        var pcm = new float[(position - pitchCursor) * recording.channels];
        if (recording.GetData(pcm, pitchCursor)) personalLearningRecorder.PushPitch(pcm, recording.channels, recording.frequency);
        else personalLearningRecorder.CancelPitch();
        pitchCursor = position;
    }

    /// <summary>
    /// WebGL辅助类
    /// </summary>
    [SerializeField]private SignalManager signalManager;
    /// <summary>
    /// 开始录制声音
    /// </summary>
    public void StartRecordAudio()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        signalManager.onAudioClipDone = null;
        signalManager.StartRecordBinding();
        personalLearningRecorder.Begin();
#else
        recording = null;
		recording = Microphone.Start(null, false, m_RecordingLength, 16000);
        if (recording != null) { personalLearningRecorder.Begin(); pitchCursor = 0; nextPitchRead = 0; pitchRecording = true; }
       
        #endif
    }

    /// <summary>
    /// 结束录制，返回audioClip
    /// </summary>
    /// <param name="_callback"></param>
    public void StopRecordAudio(Action<AudioClip> _callback)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        signalManager.onAudioClipDone += clip => { personalLearningRecorder.Seal(clip); _callback?.Invoke(clip); };
        signalManager.StopRecordBinding();
#else
        //Microphone.GetPosition必须在Microphone.End之前取，否则返回0。
        //buffer是m_RecordingLength秒的固定长度，但用户实际只说了pos个样本——
        //如果直接送整个recording给ASR，等于让服务端处理一大段静音，识别会变慢很多。
        int pos = Microphone.GetPosition(null);
        PumpPitch(pos); pitchRecording = false;
        Microphone.End(null);

        AudioClip trimmed = TrimRecording(recording, pos);
        // A zero cursor cannot prove how much of the fixed microphone buffer
        // was captured. Preserve legacy ASR fallback, exclude it from learning.
        personalLearningRecorder.Seal(pos > 0 ? trimmed : null);
        _callback(trimmed);

#endif

    }

    /// <summary>
    /// 从buffer中只截出真正录到的那一段，避免把后面的静音填充也送ASR。
    /// pos是Microphone.GetPosition返回的样本写入位置(单位：单声道样本数)。
    /// </summary>
    private AudioClip TrimRecording(AudioClip src, int pos)
    {
        if (src == null) return null;
        //pos<=0是异常情况(没拿到有效位置)，退回返回原clip保安全
        if (pos <= 0 || pos >= src.samples) return src;

        int channels = src.channels;
        int frequency = src.frequency;
        float[] data = new float[pos * channels];
        src.GetData(data, 0);

        AudioClip clip = AudioClip.Create("trimmed", pos, channels, frequency, false);
        clip.SetData(data, 0);
        return clip;
    }

}
