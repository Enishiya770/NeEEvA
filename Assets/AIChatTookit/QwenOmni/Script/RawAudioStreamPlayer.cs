using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;

public class RawAudioStreamPlayer : MonoBehaviour
{
	[SerializeField]private AudioSource audioSource;
	private List<float> audioBuffer = new List<float>();
	private int readPosition;
	private object bufferLock = new object();

	// 必须预先设置的音频参数
	public int sampleRate = 24000;
	public int channels = 1;
	public int bitDepth = 16; // 支持16-bit或32-bit

	void Awake()
	{
		InitializeAudio();
	}

	// 初始化音频流
	private void InitializeAudio()
	{
		audioSource.clip = AudioClip.Create(
			"RawStreamClip",
			int.MaxValue, // 最大长度
			channels,
			sampleRate,
			true, // 流式
			OnAudioRead
		);
		audioSource.Play();
	}

	// 添加原始PCM数据（Base64编码）
	public void AppendRawPCMData(string base64Data)
	{
		byte[] rawBytes = Convert.FromBase64String(base64Data);
		float[] audioData = ConvertRawBytesToAudioData(rawBytes, bitDepth);

		lock (bufferLock)
		{
			audioBuffer.AddRange(audioData);
		}
	}

	// 音频数据读取回调
	private void OnAudioRead(float[] data)
	{
		lock (bufferLock)
		{
			int available = audioBuffer.Count - readPosition;
			int copyLength = Mathf.Min(available, data.Length);

			if (copyLength > 0)
			{
				Array.Copy(audioBuffer.GetRange(readPosition, copyLength).ToArray(), data, copyLength);
				readPosition += copyLength;
			}

			// 填充静音
			if (copyLength < data.Length)
			{
				Array.Clear(data, copyLength, data.Length - copyLength);
			}

			// 清理已播放数据（保留最近2秒）
			if (readPosition > sampleRate * 2 * channels)
			{
				audioBuffer.RemoveRange(0, readPosition);
				readPosition = 0;
			}
		}
	}

	// 原始字节转Unity音频数据
	private float[] ConvertRawBytesToAudioData(byte[] source, int depth)
	{
		int sampleCount = depth == 16 ? source.Length / 2 : source.Length / 4;
		float[] audioData = new float[sampleCount];

		if (depth == 16)
		{
			for (int i = 0; i < sampleCount; i++)
			{
				short sample = BitConverter.ToInt16(source, i * 2);
				audioData[i] = sample / 32768.0f;
			}
		}
		else if (depth == 32)
		{
			for (int i = 0; i < sampleCount; i++)
			{
				int sample = BitConverter.ToInt32(source, i * 4);
				audioData[i] = sample / 2147483648.0f;
			}
		}

		return audioData;
	}

	// 手动清除缓冲区（可选）
	public void ClearBuffer()
	{
		lock (bufferLock)
		{
			audioBuffer.Clear();
			readPosition = 0;
		}
	}
}