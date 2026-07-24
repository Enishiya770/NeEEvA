using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Text;
using UnityEngine;

public class LLM:MonoBehaviour
{
    /// <summary>
    /// API地址。各子类在Awake()里硬编码覆盖，Inspector值不起作用，故隐藏。
    /// </summary>
    [HideInInspector]
    [SerializeField] protected string url;
    /// <summary>
    /// [回落字段] 人设prompt。仅在未设置Prompt Files时生效。
    /// ChatQW已支持Prompt Files方式，建议使用文件方式，此字段留空即可。
    /// 其他provider(Ollama/Spark/GPT等)尚未迁移，仍依赖此字段。
    /// </summary>
    [Header("[回落] 人设prompt (留空；设置了Prompt Files则忽略)")]
    [SerializeField] protected string m_Prompt = string.Empty;
    /// <summary>
    /// [回落字段] 回复语言。同上。
    /// </summary>
    [Header("[回落] 回复语言 (留空；设置了Prompt Files则忽略)")]
    [SerializeField] protected string lan="日语";
    /// <summary>
    /// 历史消息保留条数
    /// </summary>
    [Header("历史消息保留条数")]
    [SerializeField] protected int m_HistoryKeepCount = 15;
    /// <summary>
    /// 对话消息列表(运行时滚动刷新)
    /// </summary>
    [SerializeField] public List<SendData> m_DataList = new List<SendData>();
    /// <summary>
    /// 计算方法调用耗时
    /// </summary>
    [SerializeField] protected Stopwatch stopwatch=new Stopwatch();

    /// <summary>
    /// Prompt文件列表(.txt)。按顺序拼接为单条system消息注入会话。
    /// 设置后将覆盖m_Prompt+lan的每轮user消息拼接逻辑，user消息只包含原始提问。
    /// 留空则回落到旧逻辑，完全兼容老场景。
    /// </summary>
    [Header("Prompt文件(按顺序拼接为system)。设置后覆盖m_Prompt+lan拼接")]
    [SerializeField] protected TextAsset[] m_PromptFiles;

    /// <summary>
    /// 是否启用文件式system prompt
    /// </summary>
    public bool HasPromptFiles
    {
        get { return m_PromptFiles != null && m_PromptFiles.Length > 0; }
    }

    /// <summary>
    /// 把m_PromptFiles拼成一条完整的system prompt字符串。
    /// 子类可重写以自定义拼接规则(例如加分隔标题)。
    /// </summary>
    public virtual string BuildSystemPrompt()
    {
        if (!HasPromptFiles) return string.Empty;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < m_PromptFiles.Length; i++)
        {
            TextAsset ta = m_PromptFiles[i];
            if (ta == null || string.IsNullOrEmpty(ta.text)) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(ta.text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 清理m_DataList中已有的system消息，再把合成的system prompt插入到首位。
    /// 通常在子类Start()里调用一次。
    /// </summary>
    public virtual void InitSystemMessage()
    {
        string sys = BuildSystemPrompt();
        if (string.IsNullOrEmpty(sys)) return;
        for (int i = m_DataList.Count - 1; i >= 0; i--)
        {
            if (m_DataList[i] != null && m_DataList[i].role == "system")
                m_DataList.RemoveAt(i);
        }
        m_DataList.Insert(0, new SendData("system", sys));
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    public virtual void PostMsg(string _msg,Action<string> _callback) {
        //历史消息裁剪
        CheckHistory();
        string message;
        if (HasPromptFiles)
        {
            //人设已固化在system消息里，user消息只承载原始提问
            message = _msg;
        }
        else
        {
            //回落：旧的每轮拼接逻辑(给未迁移的provider用)
            message = "当前为角色的人设设定：" + m_Prompt +
                " 回复的语言：" + lan +
                " 你向我回答我的问题：" + _msg;
        }

        //保存发送的消息到列表
        m_DataList.Add(new SendData("user", message));

        StartCoroutine(Request(message, _callback));
    }

    public virtual IEnumerator Request(string _postWord, System.Action<string> _callback)
    {
        yield return new WaitForEndOfFrame();

    }

    /// <summary>
    /// 流式发送：onDelta每收到一小段增量文本触发一次；onComplete在整条回复结束时触发。
    /// 子类可覆写以实现真正的SSE/WebSocket流式；未覆写的LLM回落到非流式模式，整段一次性返回。
    /// imageDataUrl: 可选，OpenAI 多模态格式的图像 data-URL("data:image/jpeg;base64,...")——
    ///   仅多模态模型(如 Qwen3-VL) 支持。base 类不会处理它，子类自己决定如何序列化。
    /// </summary>
    public virtual void PostMsgStream(
        string _msg,
        System.Action<string> _onDelta,
        System.Action<string> _onComplete,
        string imageDataUrl = null)
    {
        PostMsg(_msg, (full) =>
        {
            if (_onDelta != null) _onDelta(full);
            if (_onComplete != null) _onComplete(full);
        });
    }

    /// <summary>
    /// 临时推理：给“用户仍在说话”的可撤销草稿使用。
    /// 实现必须保证请求和回答都不写入 m_DataList；不支持的 provider 返回空结果。
    /// </summary>
    public virtual void PostEphemeralMsg(string prompt, System.Action<string> callback)
    {
        if (callback != null) callback("");
    }

    /// <summary>用户继续说或 EOU 到达时撤销在飞的临时推理，正式回复拥有最高优先级。</summary>
    public virtual void CancelEphemeralMsg()
    {
    }

    /// <summary>
    /// 撤销当前正式回复。用户重新开口或打断角色时调用。
    /// 子类应同时停止网络请求，并保证过期回调不再写入历史或触发 TTS。
    /// </summary>
    public virtual void CancelActiveResponse()
    {
    }

    /// <summary>
    /// 维护历史消息条数，避免上下文过长
    /// </summary>
    public virtual void CheckHistory()
    {
        if(m_DataList.Count> m_HistoryKeepCount)
        {
            //跳过system消息(人设)，从第一条非system消息开始删，避免删掉人设导致角色失忆
            int startIdx = (m_DataList.Count > 0 && m_DataList[0] != null && m_DataList[0].role == "system") ? 1 : 0;
            if (m_DataList.Count > startIdx)
                m_DataList.RemoveAt(startIdx);
        }
    }

    [Serializable]
    public class SendData
    {
        [SerializeField] public string role;
        [SerializeField] public string content;
        /// <summary>
        /// 可选：附在本条 user 消息上的图像 data-URL("data:image/jpeg;base64,...")。
        /// 非 null 时 ChatQW 等多模态 provider 会按 OpenAI 多模态格式序列化 content 字段。
        /// [NonSerialized] 让 JsonUtility 不会自动塞到 JSON 里——我们走手动 JSON 路径处理它。
        /// </summary>
        [NonSerialized] public string imageDataUrl;
        public SendData() { }
        public SendData(string _role, string _content)
        {
            role = _role;
            content = _content;
        }

    }

}
