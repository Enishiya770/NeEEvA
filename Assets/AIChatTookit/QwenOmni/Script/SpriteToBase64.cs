using UnityEngine;
using System;
using System.IO;

public class SpriteToBase64
{
	/// <summary>
	/// 将Sprite转换为Base64编码字符串（自动处理不可读纹理）
	/// </summary>
	public static string OnConvert(
		Sprite sprite,
		ImageFormat format = ImageFormat.PNG,
		int jpgQuality = 75,
		bool forceCopy = true)
	{
		try
		{
			// 获取可读的Texture2D
			Texture2D texture = forceCopy ?
				GetReadableTextureCopy(sprite) :
				GetReadableTexture(sprite);

			// 编码为字节数组
			byte[] bytes = format == ImageFormat.PNG ?
				texture.EncodeToPNG() :
				texture.EncodeToJPG(jpgQuality);

			// 清理临时纹理（如果是复制的）
			if (forceCopy || !sprite.texture.isReadable)
				UnityEngine.Object.Destroy(texture);

			return Convert.ToBase64String(bytes);
		}
		catch (Exception e)
		{
			Debug.LogError($"Sprite转换失败: {e.Message}");
			return null;
		}
	}

	// 方法1：尝试直接获取纹理（不复制）
	private static Texture2D GetReadableTexture(Sprite sprite)
	{
		if (sprite == null) throw new ArgumentNullException(nameof(sprite));
		if (sprite.texture.isReadable) return sprite.texture;

		Debug.LogWarning($"纹理{sprite.texture.name}不可读，将创建临时副本");
		return GetReadableTextureCopy(sprite);
	}

	// 方法2：强制创建纹理副本（保证可读）
	private static Texture2D GetReadableTextureCopy(Sprite sprite)
	{
		// 创建新的可读纹理
		Texture2D copy = new Texture2D(
			(int)sprite.rect.width,
			(int)sprite.rect.height,
			TextureFormat.RGBA32, // 通用格式
			false                 // 不需要mipmap
		);

		try
		{
			// 获取原始像素数据
			Color[] pixels = sprite.texture.GetPixels(
				(int)sprite.textureRect.x,
				(int)sprite.textureRect.y,
				(int)sprite.textureRect.width,
				(int)sprite.textureRect.height
			);

			copy.SetPixels(pixels);
			copy.Apply();
			return copy;
		}
		catch
		{
			// 回退方案：通过RenderTexture读取
			return GetReadableTextureViaRT(sprite);
		}
	}

	// 方法3：通过RenderTexture读取（终极解决方案）
	private static Texture2D GetReadableTextureViaRT(Sprite sprite)
	{
		RenderTexture rt = RenderTexture.GetTemporary(
			sprite.texture.width,
			sprite.texture.height,
			0,
			RenderTextureFormat.Default,
			RenderTextureReadWrite.sRGB
		);

		Graphics.Blit(sprite.texture, rt);
		RenderTexture previous = RenderTexture.active;
		RenderTexture.active = rt;

		Texture2D readableTex = new Texture2D(
			(int)sprite.rect.width,
			(int)sprite.rect.height,
			TextureFormat.RGBA32,
			false
		);

		readableTex.ReadPixels(new Rect(
			sprite.rect.x,
			sprite.texture.height - sprite.rect.y - sprite.rect.height,
			sprite.rect.width,
			sprite.rect.height
		), 0, 0);

		readableTex.Apply();

		RenderTexture.active = previous;
		RenderTexture.ReleaseTemporary(rt);

		return readableTex;
	}

	public enum ImageFormat { PNG, JPG }
}