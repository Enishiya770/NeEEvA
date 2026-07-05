using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class RTChatSample : MonoBehaviour
{
    /// <summary>
    /// 聊天配置
    /// </summary>
    [SerializeField] private ChatSetting m_ChatSettings;
    #region UI定义
    /// <summary>
    /// 聊天UI窗
    /// </summary>
    [SerializeField] private GameObject m_ChatPanel;
    /// <summary>
    /// 输入的信息
    /// </summary>
    [SerializeField] public InputField m_InputWord;
    /// <summary>
    /// 返回的信息
    /// </summary>
    [SerializeField] private Text m_TextBack;
    /// <summary>
    /// 播放声音
    /// </summary>
    [SerializeField] private AudioSource m_AudioSource;
    /// <summary>
    /// 发送信息按钮
    /// </summary>
    [SerializeField] private Button m_CommitMsgBtn;

    #endregion

    #region 参数定义
    /// <summary>
    /// 动画控制器
    /// </summary>
    [SerializeField] private Animator m_Animator;
    /// <summary>
    /// 语音模式，设置为false,则不通过语音合成
    /// </summary>
    [Header("设置是否通过语音合成播放文本")]
    [SerializeField] private bool m_IsVoiceMode = true;
    /// <summary>
    /// AI回复结束之后，回调
    /// </summary>
    public Action OnAISpeakDone;

    /// <summary>
    /// 角色是否正在出声(TTS播放中)。
    /// RTSpeechHandler用这个判断"现在RMS spike算barge-in还是新一轮发言"。
    /// </summary>
    public bool IsAISpeaking { get; private set; }

    /// <summary>
    /// 当前正在播放的AI完整回复文本，被打断时按播放进度切片入聊天历史。
    /// 没人听完的尾巴不应该污染上下文记忆。
    /// </summary>
    private string m_PendingFullText = "";
    /// <summary>
    /// 当前出声协程的句柄，Interrupt时一并停掉
    /// </summary>
    private Coroutine m_TypingCoroutine;
    private Coroutine m_AudioWatchCoroutine;
    #endregion

    private void Awake()
    {
        m_CommitMsgBtn.onClick.AddListener(delegate { SendData(); });
        InputSettingWhenWebgl();
    }

    #region 消息发送

    /// <summary>
    /// webgl时处理，支持中文输入
    /// </summary>
    private void InputSettingWhenWebgl()
    {
#if UNITY_WEBGL
        m_InputWord.gameObject.AddComponent<WebGLSupport.WebGLInput>();
#endif
    }


    /// <summary>
    /// 发送信息
    /// </summary>
    public void SendData()
    {
        if (m_InputWord.text.Equals(""))
            return;

        //添加记录聊天
        m_ChatHistory.Add(m_InputWord.text);
        //提示词
        string _msg = m_InputWord.text;

        //发送数据
        m_ChatSettings.m_ChatModel.PostMsg(_msg, CallBack);

        m_InputWord.text = "";
        m_TextBack.text = "正在思考中...";

        //切换思考动作
        SetAnimator("state", 1);
    }
    /// <summary>
    /// 带文字发送
    /// </summary>
    /// <param name="_postWord"></param>
    public void SendData(string _postWord)
    {
        if (_postWord.Equals(""))
            return;

        //添加记录聊天
        m_ChatHistory.Add(_postWord);
        //提示词
        string _msg = _postWord;

        //发送数据
        m_ChatSettings.m_ChatModel.PostMsg(_msg, CallBack);

        m_InputWord.text = "";
        m_TextBack.text = "正在思考中...";

        //切换思考动作
        SetAnimator("state", 1);
    }

    /// <summary>
    /// AI回复的信息的回调
    /// </summary>
    /// <param name="_response"></param>
    private void CallBack(string _response)
    {
        _response = _response.Trim();
        m_TextBack.text = "";


        Debug.Log("收到AI回复：" + _response);

        //不在这里直接入历史——若用户中途打断，要按"实际听到的部分"入历史，
        //避免角色记忆里堆着"她以为自己说过、用户其实没听到"的内容。
        m_PendingFullText = _response;

        if (!m_IsVoiceMode || m_ChatSettings.m_TextToSpeech == null)
        {
            //纯文字模式无法被语音打断，直接入历史
            m_ChatHistory.Add(_response);
            m_PendingFullText = "";
            StartTypeWords(_response);
            return;
        }


        m_ChatSettings.m_TextToSpeech.Speak(_response, PlayVoice);
    }

    #endregion

    #region 语音输入
    /// <summary>
    /// 语音识别返回的文本是否直接发送至LLM
    /// </summary>
    [SerializeField] private bool m_AutoSend = true;
    /// <summary>
    /// 语音提示
    /// </summary>
    [SerializeField] private GameObject m_VoiceTipPanel;
    /// <summary>
    /// 录音的提示信息
    /// </summary>
    [SerializeField] private Text m_RecordTips;

    /// <summary>
    /// 处理录制的音频数据
    /// </summary>
    /// <param name="_data"></param>
    public void AcceptClip(AudioClip _audioClip)
    {
        if (m_ChatSettings.m_SpeechToText == null)
            return;

        m_RecordTips.text = "正在进行语音识别...";
        m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, DealingTextCallback);
    }
    /// <summary>
    /// 处理识别到的文本
    /// </summary>
    /// <param name="_msg"></param>
    private void DealingTextCallback(string _msg)
    {
        m_RecordTips.text = _msg;
        StartCoroutine(SetTextVisible(m_RecordTips));
        //自动发送
        if (m_AutoSend)
        {
            SendData(_msg);
            return;
        }

        m_InputWord.text = _msg;
    }

    private IEnumerator SetTextVisible(Text _textbox)
    {
        yield return new WaitForSeconds(3f);
        _textbox.text = "";
    }

    #endregion

    #region 语音合成

    private void PlayVoice(AudioClip _clip, string _response)
    {
        if (_clip == null)
        {
            //TTS失败：没声音就把文本入历史(对话不能因为TTS挂掉而丢上下文)，立即结束
            Debug.LogWarning("[RTChat] TTS返回null，回退到无声入历史");
            CommitHeardAndFinish(m_PendingFullText);
            return;
        }

        m_AudioSource.clip = _clip;
        m_AudioSource.Play();
        IsAISpeaking = true;
        Debug.Log("音频时长：" + _clip.length);
        //开始逐个显示返回的文本
        m_TypingCoroutine = StartTypeWords(_response);
        //切换到说话动作
        SetAnimator("state", 2);
        //监听音频播完(自然结束 vs Interrupt由各自路径处理)
        m_AudioWatchCoroutine = StartCoroutine(WaitForAudioDone());
    }

    /// <summary>
    /// 等到AudioSource播完整段，没被Interrupt的话就commit完整文本到历史。
    /// 被Interrupt时这个协程会先被StopCoroutine掉，不会执行到最后。
    /// </summary>
    private IEnumerator WaitForAudioDone()
    {
        //先让Play()真的开始
        yield return null;
        while (m_AudioSource.isPlaying) yield return null;
        //自然播完：完整入历史
        if (IsAISpeaking)
        {
            CommitHeardAndFinish(m_PendingFullText);
        }
    }

    /// <summary>
    /// 结束一轮出声：把"用户实际听到的文本"入历史，触发OnAISpeakDone给VAD恢复监听。
    /// 自然结束时textHeard=完整文本；被Interrupt时是按播放进度切片的部分文本。
    /// </summary>
    private void CommitHeardAndFinish(string textHeard)
    {
        IsAISpeaking = false;
        m_PendingFullText = "";

        if (!string.IsNullOrEmpty(textHeard))
        {
            m_ChatHistory.Add(textHeard);
        }

        SetAnimator("state", 0);

        if (OnAISpeakDone != null) OnAISpeakDone();
    }

    /// <summary>
    /// 用户在角色说话时插话——立即停止TTS播放、按播放进度截断文本入历史、
    /// 触发OnAISpeakDone把控制权交还给VAD路径。
    /// 没在出声时调用是no-op。
    /// </summary>
    public void Interrupt()
    {
        if (!IsAISpeaking) return;

        //估算用户听到的文本量：按音频已播放比例切原文本
        //不完美(TTS的字符密度不是均匀的)，但比"全部计入"或"全部丢弃"都更接近真相
        string heard = m_PendingFullText ?? "";
        if (m_AudioSource != null && m_AudioSource.clip != null && m_AudioSource.clip.length > 0f
            && !string.IsNullOrEmpty(m_PendingFullText))
        {
            float fraction = Mathf.Clamp01(m_AudioSource.time / m_AudioSource.clip.length);
            int charsHeard = Mathf.FloorToInt(m_PendingFullText.Length * fraction);
            heard = m_PendingFullText.Substring(0, charsHeard);
            //标记被打断，让LLM下一轮prompt明白"那句话她没说完"
            if (charsHeard < m_PendingFullText.Length) heard += "……";
        }

        Debug.Log($"[Interrupt] 角色被打断，已说: \"{heard}\"  原计划: \"{m_PendingFullText}\"");

        //停TTS、停打字机、停"等播完"协程——彻底斩断当前出声
        if (m_AudioSource != null) m_AudioSource.Stop();
        m_WriteState = false;
        if (m_TypingCoroutine != null) { StopCoroutine(m_TypingCoroutine); m_TypingCoroutine = null; }
        if (m_AudioWatchCoroutine != null) { StopCoroutine(m_AudioWatchCoroutine); m_AudioWatchCoroutine = null; }

        CommitHeardAndFinish(heard);
    }

    #endregion

    #region 文字逐个显示
    //逐字显示的时间间隔
    [SerializeField] private float m_WordWaitTime = 0.2f;
    //是否显示完成
    [SerializeField] private bool m_WriteState = false;

    /// <summary>
    /// 开始逐个打印。返回协程句柄以便Interrupt时停掉。
    /// </summary>
    /// <param name="_msg"></param>
    private Coroutine StartTypeWords(string _msg)
    {
        if (_msg == "") return null;

        m_WriteState = true;
        return StartCoroutine(SetTextPerWord(_msg));
    }

    private IEnumerator SetTextPerWord(string _msg)
    {
        int currentPos = 0;
        while (m_WriteState)
        {
            yield return new WaitForSeconds(m_WordWaitTime);
            currentPos++;
            //更新显示的内容
            m_TextBack.text = _msg.Substring(0, currentPos);

            m_WriteState = currentPos < _msg.Length;

        }

        //注意：OnAISpeakDone不在这里触发——以前是"打字机一打完就算结束"，
        //但打字速度和音频长度不一定对得上。现在统一由CommitHeardAndFinish
        //(自然播完或被Interrupt)发起，避免双重触发或抢跑。
    }

    #endregion

    #region 聊天记录
    //保存聊天记录
    [SerializeField] private List<string> m_ChatHistory;
    //缓存已创建的聊天气泡
    [SerializeField] private List<GameObject> m_TempChatBox;
    //聊天记录显示层
    [SerializeField] private GameObject m_HistoryPanel;
    //聊天文本放置的层
    [SerializeField] private RectTransform m_rootTrans;
    //发送聊天气泡
    [SerializeField] private ChatPrefab m_PostChatPrefab;
    //回复的聊天气泡
    [SerializeField] private ChatPrefab m_RobotChatPrefab;
    //滚动条
    [SerializeField] private ScrollRect m_ScroTectObject;
    //获取聊天记录
    public void OpenAndGetHistory()
    {
        m_ChatPanel.SetActive(false);
        m_HistoryPanel.SetActive(true);

        ClearChatBox();
        StartCoroutine(GetHistoryChatInfo());
    }
    //返回
    public void BackChatMode()
    {
        m_ChatPanel.SetActive(true);
        m_HistoryPanel.SetActive(false);
    }

    //清空已创建的对话框
    private void ClearChatBox()
    {
        while (m_TempChatBox.Count != 0)
        {
            if (m_TempChatBox[0])
            {
                Destroy(m_TempChatBox[0].gameObject);
                m_TempChatBox.RemoveAt(0);
            }
        }
        m_TempChatBox.Clear();
    }

    //获取聊天记录列表
    private IEnumerator GetHistoryChatInfo()
    {

        yield return new WaitForEndOfFrame();

        for (int i = 0; i < m_ChatHistory.Count; i++)
        {
            if (i % 2 == 0)
            {
                ChatPrefab _sendChat = Instantiate(m_PostChatPrefab, m_rootTrans.transform);
                _sendChat.SetText(m_ChatHistory[i]);
                m_TempChatBox.Add(_sendChat.gameObject);
                continue;
            }

            ChatPrefab _reChat = Instantiate(m_RobotChatPrefab, m_rootTrans.transform);
            _reChat.SetText(m_ChatHistory[i]);
            m_TempChatBox.Add(_reChat.gameObject);
        }

        //重新计算容器尺寸
        LayoutRebuilder.ForceRebuildLayoutImmediate(m_rootTrans);
        StartCoroutine(TurnToLastLine());
    }

    private IEnumerator TurnToLastLine()
    {
        yield return new WaitForEndOfFrame();
        //滚动到最近的消息
        m_ScroTectObject.verticalNormalizedPosition = 0;
    }


    #endregion

    private void SetAnimator(string _para, int _value)
    {
        if (m_Animator == null)
            return;

        m_Animator.SetInteger(_para, _value);
    }
}
