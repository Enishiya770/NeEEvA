using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.IO;
using System;

public class OmniChatSample : MonoBehaviour
{
	#region Params
	/// <summary>
	/// 勾选后，语音输入模式，自动调用Omni，不需要手动发送
	/// </summary>
	[Header("录音结束后，是否自动调用")]
	public bool m_AutoSend=false;
	/// <summary>
	/// 输入的文字
	/// </summary>
	[SerializeField] private InputField m_PostText;
	/// <summary>
	/// 语音输入的按钮
	/// </summary>
	[SerializeField] private Button m_VoiceInputBotton;
	/// <summary>
	/// 录音按钮的文本
	/// </summary>
	[SerializeField] private Text m_VoiceBottonText;
	/// <summary>
	/// 录音的提示信息
	/// </summary>
	[SerializeField] private Text m_RecordTips;
	[SerializeField] private GameObject m_RecordTipTranform;
	/// <summary>
	/// 语音输入处理类
	/// </summary>
	[SerializeField] private VoiceInputs m_VoiceInputs;
	/// <summary>
	/// 调用接口的方式
	/// </summary>
	[SerializeField] private Dropdown m_PostMode;

	public QwenOmni m_QweOmni;//Omi处理类
	public AudioSource m_AudioSource;
	public AudioClip m_AudioClip = null;
	public Image m_CaptureImage;//拍摄的图片

	public string m_AudioPath = "";//音频地址(本地)
	public string m_ImagePath = "";//图片地址(本地)
	public string m_VideoPath = "";//视频地址(本地)

    #endregion

    #region Method

    private void Awake()
	{
		RegistButtonEvent();
	}

	/// <summary>
	/// 发送报文
	/// </summary>
	public void OnStart()
	{
		//发送纯文本
		if (m_PostMode.value == 0)
		{
			if (m_PostText.text == "") {
				Debug.LogError("请输入问题！");
				return;
			}
			OnSendText();
            m_PostText.text = "";
            return;
		}

        //发送 文本+音频录制
        if (m_PostMode.value == 1)
        {
            if (m_AudioClip == null)
			{
                Debug.LogError("没有录入音频数据！");
                return;
			}
			OnSendAudio();
            m_PostText.text = "";
            return;
        }

        //发送 文本+本地音频
        if (m_PostMode.value == 2)
        {
            if (m_AudioPath == "")
            {
                Debug.LogError("没有选择音频路径！");
                return;
            }
            OnSendLocalAudio();
            m_PostText.text = "";
            return;
        }

        //发送 文本+本地图片
        if (m_PostMode.value == 3)
        {
			if (m_ImagePath == "")
			{
                Debug.LogError("没有选择图片路径！");
                return;
			}
			OnSendImage();
            m_PostText.text = "";
            return;
        }

        //发送 文本+本地视频
        if (m_PostMode.value == 4)
        {
            if (m_VideoPath == "")
            {
                Debug.LogError("没有选择视频路径！");
                return;
            }
			OnSendVideo();//发送视频
            m_PostText.text = "";
            return;
        }

        //发送 文本+摄像头拍摄图片
        if (m_PostMode.value == 5)
        {
            OnSendCaptureImage();//发送相机截图
            m_PostText.text = "";
            return;
        }
    }
	/// <summary>
	/// 发送文本
	/// </summary>
	private void OnSendText()
	{
	
        m_QweOmni.OnSendText(m_PostText.text, ProcessingDone);//发送纯文本
    }
	/// <summary>
	/// 发送音频
	/// </summary>
	private void OnSendAudio() {
        m_QweOmni.OnSendAudio(m_PostText.text, m_AudioClip, ProcessingDone);//发送文本+音频，文本可以为空
    }

	/// <summary>
	/// 发送本地音频
	/// </summary>
	private void OnSendLocalAudio()
	{
        string _audioPath = m_AudioPath;
        if (File.Exists(_audioPath))
        {
            try
            {
                byte[] _audipData = File.ReadAllBytes(_audioPath);
                string _base64_data = Convert.ToBase64String(_audipData);
                m_QweOmni.OnSendAudio(m_PostText.text, _base64_data, ProcessingDone);
                m_PostText.text = "";
            }
            catch (Exception e)
            {
                Debug.LogError($"转换失败: {e.Message}");
            }
        }
    }

	/// <summary>
	/// 发送图片
	/// </summary>
	private void OnSendImage()
	{
        string _imgaPath = m_ImagePath;
        if (File.Exists(_imgaPath))
        {
            try
            {
                byte[] _imgData = File.ReadAllBytes(_imgaPath);
                string _base64_data = Convert.ToBase64String(_imgData);
				m_QweOmni.OnSendImage(m_PostText.text, _base64_data, ProcessingDone);
                m_PostText.text = "";
            }
            catch (Exception e)
            {
                Debug.LogError($"转换失败: {e.Message}");
            }
        }

    }

	private void OnSendCaptureImage()
	{
        m_QweOmni.OnSendImage(m_PostText.text, m_CaptureImage.sprite, ProcessingDone);
        m_PostText.text = "";
    }

	/// <summary>
	/// 发送视频数据
	/// </summary>
	private void OnSendVideo()
	{
		string _videoPath = m_VideoPath;
		if (File.Exists(_videoPath))
		{
			try
			{
				byte[] videoData = File.ReadAllBytes(_videoPath);
				string _base64_data= Convert.ToBase64String(videoData);
				m_QweOmni.OnSendVideo(m_PostText.text, _base64_data, ProcessingDone);
				m_PostText.text = "";
			}
			catch (Exception e)
			{
				Debug.LogError($"转换失败: {e.Message}");
			}
		}
	}

	/// <summary>
	/// 图片序列
	/// </summary>
	private void OnSendImageFrame()
	{
		List<string> _frame= new List<string>();
		_frame.Add("11111");
		_frame.Add("2222");
		m_QweOmni.OnSendImageFrame(m_PostText.text, _frame, ProcessingDone);
	}


	private IEnumerator SetCaptureDisable()
	{
		yield return null;
		m_CaptureBtn.SetActive(true);
		m_CaptureObject.gameObject.SetActive(false);
		m_CaptureMode = false;
	}

	/// <summary>
	/// 注册按钮事件
	/// </summary>
	private void RegistButtonEvent()
	{
		if (m_VoiceInputBotton == null || m_VoiceInputBotton.GetComponent<EventTrigger>())
			return;

		EventTrigger _trigger = m_VoiceInputBotton.gameObject.AddComponent<EventTrigger>();

		//添加按钮按下的事件
		EventTrigger.Entry _pointDown_entry = new EventTrigger.Entry();
		_pointDown_entry.eventID = EventTriggerType.PointerDown;
		_pointDown_entry.callback = new EventTrigger.TriggerEvent();

		//添加按钮松开事件
		EventTrigger.Entry _pointUp_entry = new EventTrigger.Entry();
		_pointUp_entry.eventID = EventTriggerType.PointerUp;
		_pointUp_entry.callback = new EventTrigger.TriggerEvent();

		//添加委托事件
		_pointDown_entry.callback.AddListener(delegate { StartRecord(); });
		_pointUp_entry.callback.AddListener(delegate { StopRecord(); });

		_trigger.triggers.Add(_pointDown_entry);
		_trigger.triggers.Add(_pointUp_entry);
	}

	/// <summary>
	/// 开始录制
	/// </summary>
	public void StartRecord()
	{
		m_VoiceBottonText.text = "正在录音中...";
		m_VoiceInputs.StartRecordAudio();
	}
	/// <summary>
	/// 结束录制
	/// </summary>
	public void StopRecord()
	{
		m_VoiceBottonText.text = "按住按钮，开始录音";
		m_RecordTips.text = "已采集到录音数据，点击发送调用Qwen-Omni，或输入你的处理需求";
		m_VoiceInputs.StopRecordAudio(AcceptClip);
	}

    public void OnGetAudioPath()
    {
        OpenLocalFile _openLocalFile = new OpenLocalFile();
        m_AudioPath = _openLocalFile.OnGetAudioPath();
        m_RecordTips.text = "获取到音频路径：" + m_AudioPath;
        m_RecordTipTranform.SetActive(true);
    }

    /// <summary>
    /// 获取图片路径
    /// </summary>
    public void OnGetImagePath()
	{
        OpenLocalFile _openLocalFile= new OpenLocalFile();
        m_ImagePath = _openLocalFile.OnGetImagePath();
        m_RecordTips.text = "获取到图片路径："+ m_ImagePath;
        m_RecordTipTranform.SetActive(true);
    }
	/// <summary>
	/// 获取视频路径
	/// </summary>
    public void OnGetVideoPath()
    {
        OpenLocalFile _openLocalFile = new OpenLocalFile();
        m_VideoPath = _openLocalFile.OnGetVideoPath();
        m_RecordTips.text = "获取到视频路径：" + m_VideoPath;
        m_RecordTipTranform.SetActive(true);
    }

    public void OnplayVoice()
	{
		m_AudioSource.clip = m_AudioClip;
		m_AudioSource.Play();
	}



    #region 调用摄像头
    public CameraCapture m_CaptureObject;
	public GameObject m_CaptureBtn;
	public bool m_CaptureMode=false;
	/// <summary>
	/// 打开拍摄界面
	/// </summary>
	public void OnOpenCapture()
	{
		m_CaptureBtn.SetActive(false);
		m_CaptureObject.gameObject.SetActive(true);
		m_CaptureMode = true;
	}
    #endregion
    /// <summary>
    /// 处理录制的音频数据
    /// </summary>
    /// <param name="_data"></param>
    private void AcceptClip(AudioClip _audioClip)
	{
		m_AudioClip= _audioClip;
		if (m_AutoSend)
		{
			m_QweOmni.OnSendAudio("", m_AudioClip, ProcessingDone);
		}
	}
	
	private void ProcessingDone(string _result)
	{
		if (_result == "done")
		{
            m_AudioClip =null;
			m_RecordTips.text = "";
			m_AudioPath = "";
			m_ImagePath = "";
			m_VideoPath = "";

        }
	}


	#endregion
}
