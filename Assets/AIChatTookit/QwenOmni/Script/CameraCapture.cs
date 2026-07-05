using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using static SpriteToBase64;
using System;
public class CameraCapture : MonoBehaviour
{
	public RawImage cameraDisplay; // 用于显示摄像头画面的RawImage
	public Image photoDisplay; // 用于显示拍摄照片的Image
	public AspectRatioFitter aspectRatioFitter; // 可选，用于调整画面比例

	private WebCamTexture webCamTexture;
	private Texture2D photoTexture;
	private bool isCameraReady = false;

	void OnEnable()
	{
		// 初始化摄像头
		InitializeCamera();
	}

	void InitializeCamera()
	{
		// 检查是否有可用的摄像头
		if (WebCamTexture.devices.Length == 0)
		{
			Debug.LogError("No camera devices found");
			return;
		}

		// 获取默认摄像头（或指定摄像头）
		WebCamDevice device = WebCamTexture.devices[0];

		// 创建WebCamTexture
		webCamTexture = new WebCamTexture(device.name, Screen.width, Screen.height, 30);

		// 设置显示摄像头的RawImage
		cameraDisplay.texture = webCamTexture;

		// 开始摄像头
		webCamTexture.Play();

		// 等待摄像头准备就绪
		StartCoroutine(WaitForCameraReady());
	}

	IEnumerator WaitForCameraReady()
	{
		// 等待摄像头纹理初始化
		while (webCamTexture.width < 100)
		{
			yield return null;
		}

		// 调整显示比例
		if (aspectRatioFitter != null)
		{
			aspectRatioFitter.aspectRatio = (float)webCamTexture.width / webCamTexture.height;
		}

		isCameraReady = true;
	}

	// 拍照方法
	public void TakePhoto()
	{
		if (!isCameraReady || webCamTexture == null) return;

		// 创建新的Texture2D来存储照片
		photoTexture = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGB24, false);

		// 从WebCamTexture获取像素数据
		photoTexture.SetPixels(webCamTexture.GetPixels());
		photoTexture.Apply();

		// 将照片显示在Image组件上
		photoDisplay.sprite = Sprite.Create(photoTexture, new Rect(0, 0, photoTexture.width, photoTexture.height), new Vector2(0.5f, 0.5f));

		this.gameObject.SetActive(false);

	}


	void OnDisable()
	{
		// 停止摄像头并释放资源
		if (webCamTexture != null && webCamTexture.isPlaying)
		{
			webCamTexture.Stop();
		}
	}
}