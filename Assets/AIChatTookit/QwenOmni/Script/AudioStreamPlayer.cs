using UnityEngine;
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

[RequireComponent(typeof(AudioSource))]
public class AudioStreamPlayer : MonoBehaviour
{
	private AudioSource audioSource;
	private List<float> audioBuffer = new List<float>();
	private int readPosition;
	private object bufferLock = new object();
	private bool isInitialized;
	private int channels;
	private int sampleRate;

	void Awake()
	{
		audioSource = GetComponent<AudioSource>();
	}

	public void AppendAudioData(string base64Data)
	{
		byte[] wavBytes = Convert.FromBase64String(base64Data);

		if (!ParseWavHeader(wavBytes, out int samples, out int newSampleRate, out int newChannels, out float[] audioData))
		{
			Debug.LogError("Invalid WAV data");
			return;
		}

		lock (bufferLock)
		{
			if (!isInitialized)
			{
				InitializeAudio(newSampleRate, newChannels);
				isInitialized = true;
			}

			if (newSampleRate != sampleRate || newChannels != channels)
			{
				Debug.LogError("Audio format mismatch");
				return;
			}

			audioBuffer.AddRange(audioData);
		}
	}

	private void InitializeAudio(int sampleRate, int channels)
	{
		this.sampleRate = sampleRate;
		this.channels = channels;

		audioSource.clip = AudioClip.Create(
			"StreamClip",
			int.MaxValue,    // 足够大的长度
			channels,
			sampleRate,
			true,
			OnAudioRead
		);

		audioSource.Play();
	}

	private void OnAudioRead(float[] data)
	{
		lock (bufferLock)
		{
			int dataLength = data.Length;
			int available = audioBuffer.Count - readPosition;
			int copyLength = Mathf.Min(available, dataLength);

			if (copyLength > 0)
			{
				Array.Copy(audioBuffer.GetRange(readPosition, copyLength).ToArray(), data, copyLength);
				readPosition += copyLength;
			}

			// 填充剩余部分为静音
			if (copyLength < dataLength)
			{
				Array.Clear(data, copyLength, dataLength - copyLength);
			}

			// 定期清理已播放数据（保留最近2秒）
			if (readPosition > sampleRate * 2)
			{
				audioBuffer.RemoveRange(0, readPosition);
				readPosition = 0;
			}
		}
	}

	private bool ParseWavHeader(byte[] wavBytes, out int samples, out int sampleRate, out int channels, out float[] audioData)
	{
		// 简化的WAV头解析逻辑
		try
		{
			int headerSize = 44;
			sampleRate = BitConverter.ToInt32(wavBytes, 24);
			channels = BitConverter.ToInt16(wavBytes, 22);
			int dataSize = BitConverter.ToInt32(wavBytes, 40);

			// 提取PCM数据
			audioData = Convert16BitByteArrayToAudioData(wavBytes, headerSize, dataSize);
			samples = dataSize / 2;
			return true;
		}
		catch
		{
			sampleRate = channels = samples = 0;
			audioData = null;
			return false;
		}
	}

	private float[] Convert16BitByteArrayToAudioData(byte[] source, int headerSize, int dataSize)
	{
		int sampleCount = dataSize / 2;
		float[] audioData = new float[sampleCount];

		for (int i = 0; i < sampleCount; i++)
		{
			int offset = headerSize + i * 2;
			short sample = BitConverter.ToInt16(source, offset);
			audioData[i] = sample / 32768.0f;
		}

		return audioData;
	}
}