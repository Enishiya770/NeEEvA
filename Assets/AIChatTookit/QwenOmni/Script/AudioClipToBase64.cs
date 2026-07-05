using UnityEngine;
using System;
using System.IO;

public class AudioClipToBase64
{
	public static string ConvertToBase64WAV(AudioClip clip)
	{
		// 1. 将AudioClip转换为WAV字节数组
		byte[] wavBytes = AudioClipToWavByteArray(clip);

		// 2. 转换为Base64字符串
		string base64 = Convert.ToBase64String(wavBytes);

		return base64;
	}

	private static byte[] AudioClipToWavByteArray(AudioClip clip)
	{
		using (MemoryStream stream = new MemoryStream())
		{
			// WAV文件头长度（44字节）
			const int headerSize = 44;

			// 1. 准备PCM数据
			float[] samples = new float[clip.samples * clip.channels];
			clip.GetData(samples, 0);

			// 2. 转换为16-bit PCM字节数组
			byte[] pcmBytes = new byte[samples.Length * 2];
			int position = 0;
			foreach (float sample in samples)
			{
				short value = (short)(sample * 32767f);
				byte[] bytes = BitConverter.GetBytes(value);
				pcmBytes[position++] = bytes[0];
				pcmBytes[position++] = bytes[1];
			}

			// 3. 写入WAV头信息
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				// RIFF头
				writer.Write("RIFF".ToCharArray());
				writer.Write(pcmBytes.Length + headerSize - 8); // 文件总长度-8
				writer.Write("WAVE".ToCharArray());

				// fmt子块
				writer.Write("fmt ".ToCharArray());
				writer.Write(16); // fmt块长度
				writer.Write((ushort)1); // PCM格式
				writer.Write((ushort)clip.channels);
				writer.Write(clip.frequency);
				writer.Write(clip.frequency * clip.channels * 2); // 字节率
				writer.Write((ushort)(clip.channels * 2)); // 块对齐
				writer.Write((ushort)16); // 位深度

				// data子块
				writer.Write("data".ToCharArray());
				writer.Write(pcmBytes.Length);
				writer.Write(pcmBytes);
			}

			return stream.ToArray();
		}
	}
}