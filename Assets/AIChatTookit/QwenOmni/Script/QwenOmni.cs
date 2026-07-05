using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.UIElements;
using static QwenOmni;

public class QwenOmni : MonoBehaviour
{
    #region Params
    [SerializeField]private string url = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
	[SerializeField] private string api_key = "";
	[SerializeField] private string m_ModelName = "qwen-omni-turbo";
	[SerializeField] private VoiceType m_VoiceType = VoiceType.Cherry;
    private System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

	// 用于缓存接收数据的缓冲区
	private string dataBuffer = "";
	public Text m_Text;
	public RawAudioStreamPlayer m_AudioPlayer;

    [Header("勾选后将添加角色的设定")]
    public bool m_AddCharacterSetting = false;//添加人物设定
    [Header("添加角色设定的提示词")]
    public string m_CharacterSetting = "";//角色设定的提示词

    [Header("勾选后将发送历史记录，实现多轮对话")]
	public bool m_IsPostHistory=false;//是否发送历史记录，多轮对话
    public List<SendData> m_History = new List<SendData>();//历史记录

    #endregion
    /// <summary>
    /// 发送文本
    /// </summary>
    /// <param name="_postWord"></param>
    /// <param name="_callback"></param>
    public void OnSendText(string _postWord, System.Action<string> _callback)
	{
		m_Text.text = "";
		StartCoroutine(OnTxtRequest(_postWord, _callback));
	}

	/// <summary>
	/// 发送音频
	/// </summary>
	/// <param name="_postWord"></param>
	/// <param name="_clip"></param>
	/// <param name="_callback"></param>
	public void OnSendAudio(string _postWord, AudioClip _clip, System.Action<string> _callback)
	{
		m_Text.text = "";
        string _base64 = AudioClipToBase64.ConvertToBase64WAV(_clip);
        StartCoroutine(OnVoiceAndTextRequest(_postWord, _base64, _callback));
	}

    public void OnSendAudio(string _postWord, string _base64, System.Action<string> _callback)
    {
        m_Text.text = "";
        StartCoroutine(OnVoiceAndTextRequest(_postWord, _base64, _callback));
    }

    /// <summary>
    /// 发送图片
    /// </summary>
    /// <param name="_postWord"></param>
    /// <param name="_img"></param>
    /// <param name="_callback"></param>
    public void OnSendImage(string _postWord, Sprite _img, System.Action<string> _callback)
	{
		m_Text.text = "";
		string _base64 = SpriteToBase64.OnConvert(_img);
		StartCoroutine(OnImageAndTextRequest(_postWord, _base64, _callback));
	}

    public void OnSendImage(string _postWord, string _base64, System.Action<string> _callback)
    {
        m_Text.text = "";
        StartCoroutine(OnImageAndTextRequest(_postWord, _base64, _callback));
    }

    /// <summary>
    /// 发送视频
    /// </summary>
    /// <param name="_postWord"></param>
    /// <param name="_video_base64"></param>
    /// <param name="_callback"></param>
    public void OnSendVideo(string _postWord, string _video_base64, System.Action<string> _callback)
	{
		m_Text.text = "";
		StartCoroutine(OnVideoAndTextRequest(_postWord, _video_base64, _callback));
	}

	/// <summary>
	/// 发送图片序列
	/// </summary>
	/// <param name="_postWord"></param>
	/// <param name="_img_base64"></param>
	/// <param name="_callback"></param>
	public void OnSendImageFrame(string _postWord, List<string> _img_base64, System.Action<string> _callback)
	{
		m_Text.text = "";
		StartCoroutine(OnImageFrameAndTextRequest(_postWord, _img_base64, _callback));
	}

	/// <summary>
	/// 添加回答到历史记录
	/// </summary>
	/// <param name="_text"></param>
	public void OnAddResponse()
	{
        var _response = new SendData();
        _response.role = "assistant";
        TextContentData _textContent = new TextContentData();
        _textContent.text = m_Text.text;
        _response.content.Add(_textContent);

        //添加到对话记录
        m_History.Add(_response);
    }
	/// <summary>
	/// 添加人设
	/// </summary>
	public void OnAddCharacterSetting(ref List<SendData> _datas)
	{
		//添加角色设定提示词
        var _character = new SendData();
        _character.role = "user";
        TextContentData _characterContent = new TextContentData();
        _characterContent.text = m_CharacterSetting;
        _character.content.Add(_characterContent);
        //添加系统回复
        var _response = new SendData();
        _response.role = "assistant";
        TextContentData _responseContent = new TextContentData();
        _responseContent.text = "好的，我记住了你的设定.";
        _response.content.Add(_responseContent);

        _datas.Add(_character);
        _datas.Add(_response);
    }

	/// <summary>
	/// 附加历史对话记录，实现多轮对话
	/// </summary>
	public void OnAddHistoryData(ref List<SendData> _datas)
	{
		foreach(var item in m_History)
		{
            _datas.Add(item);

        }
	}


	#region 处理方法

	/// <summary>
	/// 只发送文本
	/// </summary>
	/// <param name="_postWord"></param>
	/// <param name="_callback"></param>
	/// <returns></returns>
	public IEnumerator OnTxtRequest(string _postWord, System.Action<string> _callback)
	{
		List<SendData> _list= new List<SendData>();
		OnAddHistoryData(ref _list);

		stopwatch.Restart();
		using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
		{

            PostTextData _postData = new PostTextData();//发送报文

			if (m_AddCharacterSetting)
			{
				OnAddCharacterSetting(ref _postData.messages);//添加人设
			}

			if (m_IsPostHistory) 
			{
				OnAddHistoryData(ref _postData.messages);	//附加历史记录
            }

            // 初始化请求头和数据 
            var _sendWord = new SendData();
			_sendWord.role = "user";
			TextContentData _textContent= new TextContentData();
			_textContent.text = _postWord;
			_sendWord.content.Add(_textContent);

			//添加到对话记录
			m_History.Add(_sendWord);
		
			_postData.model = m_ModelName;
			_postData.messages.Add(_sendWord);
			_postData.stream = true;
			_postData.modalities.Add("text");
			_postData.modalities.Add("audio");
			
			_postData.audio.voice = m_VoiceType.ToString();//音色

            string _jsonText = JsonConvert.SerializeObject(_postData);
			//string _jsonText = JsonUtility.ToJson(_postData);
			byte[] data = Encoding.UTF8.GetBytes(_jsonText);

			request.uploadHandler = new UploadHandlerRaw(data);
			request.downloadHandler = new DownloadHandlerBuffer();

			request.SetRequestHeader("Content-Type", "application/json");
			request.SetRequestHeader("Authorization", $"Bearer {api_key}");

			// 异步发送请求
			request.SendWebRequest();
			int bytesReceived = 0;

			// 实时处理流数据
			while (!request.isDone)
			{
				// 获取最新接收的字节数
				int newBytes = request.downloadHandler.data != null ? request.downloadHandler.data.Length : 0;
				if (newBytes > bytesReceived)
				{
					// 提取新增数据并转换
					byte[] newData = new byte[newBytes - bytesReceived];
					Array.Copy(request.downloadHandler.data, bytesReceived, newData, 0, newData.Length);
					string chunk = Encoding.UTF8.GetString(newData);

					// 处理数据块
					ProcessChunk(chunk, _callback);
					bytesReceived = newBytes;
				}
				yield return null;
			}

			// 处理剩余数据
			if (request.downloadHandler.data != null && bytesReceived < request.downloadHandler.data.Length)
			{
				byte[] remainingData = new byte[request.downloadHandler.data.Length - bytesReceived];
				Array.Copy(request.downloadHandler.data, bytesReceived, remainingData, 0, remainingData.Length);
				ProcessChunk(Encoding.UTF8.GetString(remainingData), _callback);
			}

			// 错误处理
			if (request.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError($"Error: {request.error}");
			}
		}
		stopwatch.Stop();
		Debug.Log($"Total time: {stopwatch.Elapsed.TotalSeconds}s");
	}

    /// <summary>
    /// 发送音频以及文本
    /// </summary>
    /// <param name="_postWord"></param>
    /// <param name="_base64"></param>
    /// <param name="_callback"></param>
    /// <returns></returns>
    public IEnumerator OnVoiceAndTextRequest(string _postWord, string _base64,System.Action<string> _callback)
	{
		stopwatch.Restart();
		using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
		{
			if (_postWord == "") { _postWord = "请根据语音内容进行回答"; }

            PostTextData _postData = new PostTextData();//发送报文
            if (m_AddCharacterSetting)
            {
                OnAddCharacterSetting(ref _postData.messages);//添加人设
            }
            if (m_IsPostHistory)
            {
                OnAddHistoryData(ref _postData.messages);
            }

            // 初始化请求头和数据
            var _sendWord = new SendData();
			_sendWord.role = "user";
			VoiceContentData _voiceContent=new VoiceContentData();
			_sendWord.content.Add(_voiceContent);
			_voiceContent.input_audio.data += _base64;

			//添加文本
			TextContentData _textContent=new TextContentData();
			_textContent.text = _postWord;
			_sendWord.content.Add(_textContent);

            //添加到对话记录
            m_History.Add(_sendWord);

			_postData.model = m_ModelName;
			_postData.messages.Add(_sendWord);
			_postData.stream = true;
			_postData.modalities.Add("text");
			_postData.modalities.Add("audio");
            _postData.audio.voice = m_VoiceType.ToString();//音色

            string _jsonText = JsonConvert.SerializeObject( _postData );
			//string _jsonText = JsonUtility.ToJson(_postData);
			byte[] data = Encoding.UTF8.GetBytes(_jsonText);

			request.uploadHandler = new UploadHandlerRaw(data);
			request.downloadHandler = new DownloadHandlerBuffer();

			request.SetRequestHeader("Content-Type", "application/json");
			request.SetRequestHeader("Authorization", $"Bearer {api_key}");

			// 异步发送请求
			request.SendWebRequest();
			int bytesReceived = 0;

			// 实时处理流数据
			while (!request.isDone)
			{
				// 获取最新接收的字节数
				int newBytes = request.downloadHandler.data != null ? request.downloadHandler.data.Length : 0;
				if (newBytes > bytesReceived)
				{
					// 提取新增数据并转换
					byte[] newData = new byte[newBytes - bytesReceived];
					Array.Copy(request.downloadHandler.data, bytesReceived, newData, 0, newData.Length);
					string chunk = Encoding.UTF8.GetString(newData);

					// 处理数据块
					ProcessChunk(chunk, _callback);
					bytesReceived = newBytes;
				}
				yield return null;
			}

			// 处理剩余数据
			if (request.downloadHandler.data != null && bytesReceived < request.downloadHandler.data.Length)
			{
				byte[] remainingData = new byte[request.downloadHandler.data.Length - bytesReceived];
				Array.Copy(request.downloadHandler.data, bytesReceived, remainingData, 0, remainingData.Length);
				ProcessChunk(Encoding.UTF8.GetString(remainingData), _callback);
			}

			// 错误处理
			if (request.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError($"Error: {request.error}");
			}
		}
		stopwatch.Stop();
		Debug.Log($"Total time: {stopwatch.Elapsed.TotalSeconds}s");
	}

    /// <summary>
    /// 发送图片与文本
    /// </summary>
    /// <param name="_postWord"></param>
    /// <param name="_img_base64"></param>
    /// <param name="_callback"></param>
    /// <returns></returns>
    public IEnumerator OnImageAndTextRequest(string _postWord,string _img_base64, System.Action<string> _callback)
	{
		stopwatch.Restart();
		using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
		{
            PostTextData _postData = new PostTextData();//发送报文
            if (m_AddCharacterSetting)
            {
                OnAddCharacterSetting(ref _postData.messages);//添加人设
            }
            if (m_IsPostHistory)
            {
                OnAddHistoryData(ref _postData.messages);
            }

            // 初始化请求头和数据
            var _sendWord = new SendData();
			_sendWord.role = "user";
			ImageContentData _imgContent = new ImageContentData();
			_sendWord.content.Add(_imgContent);
			_imgContent.image_url.url += _img_base64;

			//添加文本
			TextContentData _textContent = new TextContentData();
			_textContent.text = _postWord;
			_sendWord.content.Add(_textContent);

            //添加到对话记录
            m_History.Add(_sendWord);

			_postData.model = m_ModelName;
			_postData.messages.Add(_sendWord);
			_postData.stream = true;
			_postData.modalities.Add("text");
			_postData.modalities.Add("audio");
            _postData.audio.voice = m_VoiceType.ToString();//音色

            string _jsonText = JsonConvert.SerializeObject(_postData);
			//string _jsonText = JsonUtility.ToJson(_postData);
			byte[] data = Encoding.UTF8.GetBytes(_jsonText);

			request.uploadHandler = new UploadHandlerRaw(data);
			request.downloadHandler = new DownloadHandlerBuffer();

			request.SetRequestHeader("Content-Type", "application/json");
			request.SetRequestHeader("Authorization", $"Bearer {api_key}");

			// 异步发送请求
			request.SendWebRequest();
			int bytesReceived = 0;

			// 实时处理流数据
			while (!request.isDone)
			{
				// 获取最新接收的字节数
				int newBytes = request.downloadHandler.data != null ? request.downloadHandler.data.Length : 0;
				if (newBytes > bytesReceived)
				{
					// 提取新增数据并转换
					byte[] newData = new byte[newBytes - bytesReceived];
					Array.Copy(request.downloadHandler.data, bytesReceived, newData, 0, newData.Length);
					string chunk = Encoding.UTF8.GetString(newData);

					// 处理数据块
					ProcessChunk(chunk, _callback);
					bytesReceived = newBytes;
				}
				yield return null;
			}

			// 处理剩余数据
			if (request.downloadHandler.data != null && bytesReceived < request.downloadHandler.data.Length)
			{
				byte[] remainingData = new byte[request.downloadHandler.data.Length - bytesReceived];
				Array.Copy(request.downloadHandler.data, bytesReceived, remainingData, 0, remainingData.Length);
				ProcessChunk(Encoding.UTF8.GetString(remainingData), _callback);
			}

			// 错误处理
			if (request.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError($"Error: {request.error}");
			}
		}
		stopwatch.Stop();
		Debug.Log($"Total time: {stopwatch.Elapsed.TotalSeconds}s");
	}

	/// <summary>
	/// 发送视频与文本
	/// </summary>
	/// <param name="_postWord"></param>
	/// <param name="_video_base64"></param>
	/// <param name="_callback"></param>
	/// <returns></returns>
	public IEnumerator OnVideoAndTextRequest(string _postWord, string _video_base64, System.Action<string> _callback)
	{
		stopwatch.Restart();
		using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
		{
            PostTextData _postData = new PostTextData();//发送报文                                            
            if (m_AddCharacterSetting)
            {
                OnAddCharacterSetting(ref _postData.messages);//添加人设
            }
            if (m_IsPostHistory)
            {
                OnAddHistoryData(ref _postData.messages);
            }
            // 初始化请求头和数据
            var _sendWord = new SendData();
			_sendWord.role = "user";
			VideoContentData _videoContent = new VideoContentData();
			_sendWord.content.Add(_videoContent);
			_videoContent.video_url.url += _video_base64;

			//添加文本
			TextContentData _textContent = new TextContentData();
			_textContent.text = _postWord;
			_sendWord.content.Add(_textContent);

            //添加到对话记录
            m_History.Add(_sendWord);

			_postData.model = m_ModelName;
			_postData.messages.Add(_sendWord);
			_postData.stream = true;
			_postData.modalities.Add("text");
			_postData.modalities.Add("audio");
            _postData.audio.voice = m_VoiceType.ToString();//音色

            string _jsonText = JsonConvert.SerializeObject(_postData);
			//string _jsonText = JsonUtility.ToJson(_postData);
			byte[] data = Encoding.UTF8.GetBytes(_jsonText);

			request.uploadHandler = new UploadHandlerRaw(data);
			request.downloadHandler = new DownloadHandlerBuffer();

			request.SetRequestHeader("Content-Type", "application/json");
			request.SetRequestHeader("Authorization", $"Bearer {api_key}");

			// 异步发送请求
			request.SendWebRequest();
			int bytesReceived = 0;

			// 实时处理流数据
			while (!request.isDone)
			{
				// 获取最新接收的字节数
				int newBytes = request.downloadHandler.data != null ? request.downloadHandler.data.Length : 0;
				if (newBytes > bytesReceived)
				{
					// 提取新增数据并转换
					byte[] newData = new byte[newBytes - bytesReceived];
					Array.Copy(request.downloadHandler.data, bytesReceived, newData, 0, newData.Length);
					string chunk = Encoding.UTF8.GetString(newData);

					// 处理数据块
					ProcessChunk(chunk, _callback);
					bytesReceived = newBytes;
				}
				yield return null;
			}

			// 处理剩余数据
			if (request.downloadHandler.data != null && bytesReceived < request.downloadHandler.data.Length)
			{
				byte[] remainingData = new byte[request.downloadHandler.data.Length - bytesReceived];
				Array.Copy(request.downloadHandler.data, bytesReceived, remainingData, 0, remainingData.Length);
				ProcessChunk(Encoding.UTF8.GetString(remainingData), _callback);
			}

			// 错误处理
			if (request.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError($"Error: {request.error}");
			}
		}
		stopwatch.Stop();
		Debug.Log($"Total time: {stopwatch.Elapsed.TotalSeconds}s");
	}

	/// <summary>
	/// 发送图片序列
	/// </summary>
	/// <param name="_postWord"></param>
	/// <param name="_img_base64"></param>
	/// <param name="_callback"></param>
	/// <returns></returns>
	public IEnumerator OnImageFrameAndTextRequest(string _postWord, List<string> _img_base64, System.Action<string> _callback)
	{
		stopwatch.Restart();
		using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
		{
            PostTextData _postData = new PostTextData();//发送报文                                            
            if (m_AddCharacterSetting)
            {
                OnAddCharacterSetting(ref _postData.messages);//添加人设
            }
            if (m_IsPostHistory)
            {
                OnAddHistoryData(ref _postData.messages);
            }
            // 初始化请求头和数据  
            var _sendWord = new SendData();
			_sendWord.role = "user";
			ImageFrameContentData _imageFrameContent = new ImageFrameContentData();
			_sendWord.content.Add(_imageFrameContent);
			foreach(var item in _img_base64)
			{
				string _val = "data:image/jpeg;base64," + item;
				_imageFrameContent.video.Add(_val);
			}

			//添加文本
			TextContentData _textContent = new TextContentData();
			_textContent.text = _postWord;
			_sendWord.content.Add(_textContent);

            //添加到对话记录
            m_History.Add(_sendWord);

			_postData.model = m_ModelName;
			_postData.messages.Add(_sendWord);
			_postData.stream = true;
			_postData.modalities.Add("text");
			_postData.modalities.Add("audio");
            _postData.audio.voice = m_VoiceType.ToString();//音色

            string _jsonText = JsonConvert.SerializeObject(_postData);

			//string _jsonText = JsonUtility.ToJson(_postData);
			byte[] data = Encoding.UTF8.GetBytes(_jsonText);

			request.uploadHandler = new UploadHandlerRaw(data);
			request.downloadHandler = new DownloadHandlerBuffer();

			request.SetRequestHeader("Content-Type", "application/json");
			request.SetRequestHeader("Authorization", $"Bearer {api_key}");

			// 异步发送请求
			request.SendWebRequest();
			int bytesReceived = 0;

			// 实时处理流数据
			while (!request.isDone)
			{
				// 获取最新接收的字节数
				int newBytes = request.downloadHandler.data != null ? request.downloadHandler.data.Length : 0;
				if (newBytes > bytesReceived)
				{
					// 提取新增数据并转换
					byte[] newData = new byte[newBytes - bytesReceived];
					Array.Copy(request.downloadHandler.data, bytesReceived, newData, 0, newData.Length);
					string chunk = Encoding.UTF8.GetString(newData);

					// 处理数据块
					ProcessChunk(chunk, _callback);
					bytesReceived = newBytes;
				}
				yield return null;
			}

			// 处理剩余数据
			if (request.downloadHandler.data != null && bytesReceived < request.downloadHandler.data.Length)
			{
				byte[] remainingData = new byte[request.downloadHandler.data.Length - bytesReceived];
				Array.Copy(request.downloadHandler.data, bytesReceived, remainingData, 0, remainingData.Length);
				ProcessChunk(Encoding.UTF8.GetString(remainingData), _callback);
			}

			// 错误处理
			if (request.result != UnityWebRequest.Result.Success)
			{
				Debug.LogError($"Error: {request.error}");
			}
		}
		stopwatch.Stop();
		Debug.Log($"Total time: {stopwatch.Elapsed.TotalSeconds}s");
	}

	#endregion

	/// <summary>
	/// 处理返回的数据块
	/// </summary>
	/// <param name="chunk"></param>
	/// <param name="callback"></param>
	private void ProcessChunk(string chunk, System.Action<string> callback)
	{
		dataBuffer += chunk;

		// 按事件分隔符处理（假设使用\n\n作为分隔符）
		while (true)
		{
			int endIndex = dataBuffer.IndexOf("\n\n");
			if (endIndex == -1) break;

			// 提取单个事件
			string rawEvent = dataBuffer.Substring(0, endIndex).Trim();
			dataBuffer = dataBuffer.Substring(endIndex + 2);

			// 处理有效事件
			if (rawEvent.StartsWith("data: "))
			{
				string jsonContent = rawEvent.Substring(6); // 去除"data: "前缀
				if (jsonContent == "[DONE]")//接收完成
				{
					Debug.Log("数据接收完成！");
					OnAddResponse();//添加回答的完整文本到历史记录
                    callback("done");
					break;
				}
				HandleJsonEvent(jsonContent, callback);
			}
		}
	}

	private void HandleJsonEvent(string json, System.Action<string> callback)
	{
		try
		{
			MessageBack result = JsonUtility.FromJson<MessageBack>(json);
			if (result?.choices?.Count > 0)
			{
				var delta = result.choices[0].delta;
				string content = delta.audio.transcript;
				//如果有文本数据
				if (content != "")
				{
					m_Text.text += content;
					Debug.Log($"Received: {content}");
					callback?.Invoke(content);
				}
				
				//如果有音频数据
				if (delta.audio.data != "")
				{
					m_AudioPlayer.AppendRawPCMData(delta.audio.data);
				}
				
				
			}
		}
		catch (Exception e)
		{
			Debug.LogError($"JSON解析失败: {e.Message}");
		}
	}


	// 以下为数据结构定义
	[Serializable]
	public class SendData
	{
		public string role;
		public List<ContentData> content=new List<ContentData>();
	}

	[Serializable]
	public class PostTextData
	{
		public string model;
		public List<SendData> messages = new List<SendData>();
		public bool stream = true;
		public List<string> modalities = new List<string>();
		public AudioSet audio= new AudioSet();
	}
	[Serializable]
	public class ContentData
	{
		public string type = "";
	}

	/// <summary>
	/// 文本类型报文
	/// </summary>
	[Serializable]
	public class TextContentData : ContentData
	{
		public TextContentData() { type = "text"; }
		public string text = "";
	}
	/// <summary>
	/// 文本+音频
	/// </summary>
	[Serializable]
	public class VoiceContentData: ContentData
	{
		public VoiceContentData() { type = "input_audio"; }
		public AudioInput input_audio=new AudioInput();
	}
	[Serializable]
	public class AudioInput
	{
		public string data = "data:;base64,";//,后添加base64音频编码
		public string format = "wav";//wav,mp3
	}

	/// <summary>
	/// 文本+图片
	/// </summary>
	[Serializable]
	public class ImageContentData : ContentData
	{
		public ImageContentData() { type = "image_url"; }
		public ImageInput image_url = new ImageInput();
	}

	public class ImageInput
	{
		public string url = "data:image/png;base64,";//，后添加base64图片编码
	}

	/// <summary>
	/// 文本+视频
	/// </summary>
	[Serializable]
	public class VideoContentData : ContentData
	{
		public VideoContentData() { type = "video_url"; }
		public VideoInput video_url = new VideoInput();
	}
	[Serializable]
	public class VideoInput
	{
		public string url = "data:;base64,";//，后添加base64视频编码
	}

	/// <summary>
	/// 文本+图片序列
	/// </summary>
	[Serializable]
	public class ImageFrameContentData : ContentData
	{
		public ImageFrameContentData() { type = "video"; }
		public List<string> video = new List<string>();//   "data:image/jpeg;base64,";//，后添加base64图片编码
	}


	[Serializable]
	public class MessageBack
	{
		public List<Choice> choices = new List<Choice>();
	}

	[Serializable]
	public class Choice
	{
		public Delta delta=new Delta();
	}

	[Serializable]
	public class Delta
	{
		public string role= string.Empty;
		public string content= string.Empty;
		public Audio audio=new Audio();
	}

	[Serializable]
	public class AudioSet
	{
		public string voice = "Cherry";//Cherry、Serena、Ethan、Chelsie
        public string format = "wav";
	}

	[Serializable]
	public class Audio
	{
		public string transcript="";
		public string data="";
	}

	/// <summary>
	/// 音色
	/// </summary>
	public enum VoiceType
	{
        Cherry,
        Serena,
        Ethan,
        Chelsie
    }

}
