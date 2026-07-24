using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class ChatQW : LLM
{
    public enum BackendType { Cloud, Local }

    private UnityWebRequest m_ActiveStreamRequest;
    private int m_StreamRequestGeneration = 0;

    [Header("后端选择: Cloud=阿里云百炼API / Local=本机llama-server")]
    public BackendType m_Backend = BackendType.Cloud;

    [Header("[本地] llama-server URL (默认8080; Ollama请改为11434)")]
    public string m_LocalUrl = "http://127.0.0.1:8080/v1/chat/completions";

    [Header("[本地] 本地服务端的模型别名 (llama-server任意值均可, Ollama用ollama list里的名字)")]
    public string m_LocalModelName = "qwen36";

    void Awake()
    {
        if (m_Backend == BackendType.Local)
        {
            url = m_LocalUrl;
            Debug.Log("[ChatQW] 后端=Local llama-server, URL=" + url + ", model=" + m_LocalModelName);
        }
        else
        {
            url = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
            Debug.Log("[ChatQW] 后端=Cloud 百炼, model=" + m_ChatModelName);
        }
    }

    /// <summary>
    /// 运行时取决于 m_Backend 返回实际该用的模型名
    /// </summary>
    private string CurrentModelName
    {
        get { return m_Backend == BackendType.Local ? m_LocalModelName : m_ChatModelName; }
    }

    /// <summary>
    /// 手动拼请求 JSON——之前用 JsonUtility 序列化 PostData，但 JsonUtility 处理不了
    /// OpenAI 多模态消息的混合 content 字段(string vs array of parts)，所以走手写。
    ///
    /// 规则：
    /// - role/系统消息: content 直接是字符串(传统 OpenAI 格式)
    /// - 带图的 user 消息: content 是数组 [{"type":"text",...},{"type":"image_url",...}]
    /// - llama-server 在 Local 后端额外注入 chat_template_kwargs 让 enable_thinking 生效
    ///
    /// 副作用：发送前会调 PruneOldImagesInPlace 把历史里超出 m_KeepRecentImages 的旧图剥掉，
    /// 避免视觉 token 累积爆掉上下文。
    /// </summary>
    private string BuildRequestJson(bool stream)
    {
        PruneOldImagesInPlace(m_DataList, m_KeepRecentImages);

        var sb = new StringBuilder(2048);
        sb.Append('{');
        sb.Append("\"model\":");
        AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":").Append(stream ? "true" : "false");
        //顶层 enable_thinking 给 DashScope 用；Local 后端会再注入 chat_template_kwargs(下方)
        sb.Append(",\"enable_thinking\":").Append(m_EnableThinking ? "true" : "false");
        sb.Append(",\"messages\":[");
        for (int i = 0; i < m_DataList.Count; i++)
        {
            var msg = m_DataList[i];
            if (msg == null) continue;
            if (sb[sb.Length - 1] != '[') sb.Append(',');
            AppendMessage(sb, msg);
        }
        sb.Append(']');
        if (m_Backend == BackendType.Local)
        {
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":")
              .Append(m_EnableThinking ? "true" : "false")
              .Append('}');
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 序列化一条消息。无 imageDataUrl 走传统 string content；有就用 OpenAI 多模态 array 格式。
    /// </summary>
    private static void AppendMessage(StringBuilder sb, SendData msg)
    {
        sb.Append('{');
        sb.Append("\"role\":");
        AppendJsonString(sb, msg.role);
        sb.Append(",\"content\":");
        if (string.IsNullOrEmpty(msg.imageDataUrl))
        {
            //传统单字符串 content
            AppendJsonString(sb, msg.content ?? "");
        }
        else
        {
            //多模态 array content：先文字再图像(OpenAI 推荐顺序)
            sb.Append("[{\"type\":\"text\",\"text\":");
            AppendJsonString(sb, msg.content ?? "");
            sb.Append("},{\"type\":\"image_url\",\"image_url\":{\"url\":");
            AppendJsonString(sb, msg.imageDataUrl);
            sb.Append("}}]");
        }
        sb.Append('}');
    }

    /// <summary>
    /// 滑窗：保留最近 keepN 条带图 user 消息的 imageDataUrl，更老的清掉(只留文字)。
    /// 防止视觉 token 累积——每帧 1024×576 截图大约 ~600-1000 token。
    /// </summary>
    private static void PruneOldImagesInPlace(List<SendData> messages, int keepN)
    {
        if (messages == null || keepN < 0) return;
        //从尾到头扫，遇到带图的 user 消息计数；超出保留数的清掉 imageDataUrl
        int kept = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m == null) continue;
            if (string.IsNullOrEmpty(m.imageDataUrl)) continue;
            kept++;
            if (kept > keepN)
            {
                m.imageDataUrl = null;
            }
        }
    }

    /// <summary>
    /// 错误诊断用：把 JSON 请求体里 data:image/...;base64,... 的 base64 替换成长度占位，
    /// 让 LogError 能打出可读的结构而不是 200KB 的 base64 噪声。
    /// </summary>
    private static string SummarizeRequestBody(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        var re = new System.Text.RegularExpressions.Regex(
            @"""data:image/[^;]+;base64,[^""]+""");
        return re.Replace(json, m => "\"data:image/...;base64,<" + (m.Value.Length - 30) + " chars>\"");
    }

    /// <summary>
    /// JSON 字符串值的标准转义。Append 到 sb，自带首尾双引号。
    /// </summary>
    private static void AppendJsonString(StringBuilder sb, string s)
    {
        if (s == null) { sb.Append("null"); return; }
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// [回落字段] AI system设定。仅在未配置Prompt Files时生效。
    /// 推荐使用Prompt Files，此字段留空即可。
    /// </summary>
    [Header("[回落] System设定 (留空；设置了Prompt Files则忽略)")]
    public string m_SystemSetting = string.Empty;
    [Header("[云端] 模型名称请到阿里云百炼平台查阅接口文档")]
    public string m_ChatModelName = "qwen-turbo";
    /// <summary>
    /// api key (本地llama-server/Ollama无需填写，填什么都行)
    /// </summary>
    public string api_key = "";
    [Header("Qwen3/3.6思考模式。关闭可大幅缩短首token延迟 (云端/本地llama-server均生效)")]
    public bool m_EnableThinking = false;

    [Header("多模态: 历史里最多保留多少帧带图的 user 消息(更老的剥掉只留文字)")]
    [Tooltip("视觉 token 很贵，全保留会爆上下文。默认 2 让最近一两帧能精读，更早的留文字记忆")]
    public int m_KeepRecentImages = 2;

    [Header("低延迟模式：请求中最多保留的非system历史消息数")]
    [Tooltip("包含即将加入的本轮user和assistant。8约等于最近3轮完整对话+当前轮，能明显限制prefill耗时")]
    [Range(4, 20)] public int m_LowLatencyHistoryLimit = 8;

    [Header("Debug：打印LLM请求大小/消息数（不打印正文和密钥）")]
    public bool m_LogRequestStats = true;

    [Header("流式倾听临时草稿")]
    [Tooltip("临时请求的最大输出 token。它不进入历史，并会在 EOU 时被撤销。")]
    [Range(48, 256)] public int m_EphemeralMaxTokens = 128;
    [Tooltip("临时请求最多携带多少条最近的非 system 消息。")]
    [Range(0, 8)] public int m_EphemeralHistoryMessages = 4;

    private UnityWebRequest m_EphemeralRequest;
    private int m_EphemeralGeneration = 0;

    private void Start()
    {
        //运行时，添加AI设定
        if (HasPromptFiles)
        {
            //使用Prompt文件合成system消息(推荐)
            InitSystemMessage();
        }
        else
        {
            //回落：使用Inspector上的m_SystemSetting
            m_DataList.Add(new SendData("system", m_SystemSetting));
        }
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    /// <returns></returns>
    public override void PostMsg(string _msg, Action<string> _callback)
    {
        CancelEphemeralMsg();
        base.PostMsg(_msg, _callback);
    }


    /// <summary>
    /// 发送数据
    /// </summary>
    /// <param name="_postWord"></param>
    /// <param name="_callback"></param>
    /// <returns></returns>
    public override IEnumerator Request(string _postWord, System.Action<string> _callback)
    {
        stopwatch.Start();
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            string _jsonText = BuildRequestJson(stream: false);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(_jsonText);
            request.uploadHandler = (UploadHandler)new UploadHandlerRaw(data);
            request.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();

            request.SetRequestHeader("Content-Type", "application/json");
            //本地Ollama不校验Bearer，填任意值无影响
            request.SetRequestHeader("Authorization", string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));

            yield return request.SendWebRequest();

            if (request.responseCode == 200)
            {
                string _msgBack = request.downloadHandler.text;
                MessageBack _textback = JsonUtility.FromJson<MessageBack>(_msgBack);
                if (_textback != null && _textback.choices.Count > 0)
                {

                    string _backMsg = _textback.choices[0].message.content;
                    //添加记录
                    m_DataList.Add(new SendData("assistant", _backMsg));
                    _callback(_backMsg);
                }
            }
            else
            {
                string _msgBack = request.downloadHandler.text;
                Debug.LogError(_msgBack);
            }

            stopwatch.Stop();
            Debug.Log("chat百度-耗时：：" + stopwatch.Elapsed.TotalSeconds);
        }
    }

    #region 流式实现

    /// <summary>
    /// 流式发送，边吐token边触发回调。
    /// imageDataUrl 可选——传入则会作为多模态消息附图(需多模态模型如 Qwen3-VL 支持)。
    /// </summary>
    public override void PostMsgStream(string _msg, Action<string> _onDelta, Action<string> _onComplete, string imageDataUrl = null)
    {
        CancelEphemeralMsg();
        //同一时刻只允许一个正式回复。旧 user 消息保留在上下文中，作为用户继续补充
        //的前半句；旧请求的 assistant 回调则必须彻底失效，避免回答乱序。
        CancelActiveResponse();
        int generation = m_StreamRequestGeneration;
        CheckHistory();
        string message;
        if (HasPromptFiles)
        {
            //人设已固化在system消息里，user消息只承载原始提问
            //减少输入token => 降低首token延迟 + 提升服务端prompt cache命中率
            message = _msg;
        }
        else
        {
            //回落：旧的每轮拼接逻辑
            message = "当前为角色的人设设定：" + m_Prompt +
                " 回复的语言：" + lan +
                " 你向我回答我的问题：" + _msg;
        }
        var entry = new SendData("user", message);
        entry.imageDataUrl = imageDataUrl;
        m_DataList.Add(entry);
        StartCoroutine(RequestStream(message, generation, _onDelta, _onComplete));
    }

    public override void CancelActiveResponse()
    {
        m_StreamRequestGeneration++;
        if (m_ActiveStreamRequest != null)
        {
            try
            {
                if (!m_ActiveStreamRequest.isDone) m_ActiveStreamRequest.Abort();
            }
            catch (Exception) { }
            m_ActiveStreamRequest = null;
        }
    }

    /// <summary>
    /// 用当前角色 system prompt 和少量最近对话生成可撤销草稿；不调用 CheckHistory、
    /// 不向 m_DataList 添加 user/assistant，因此猜错的 partial 永远不会成为角色记忆。
    /// </summary>
    public override void PostEphemeralMsg(string prompt, Action<string> callback)
    {
        CancelEphemeralMsg();
        int generation = m_EphemeralGeneration;
        StartCoroutine(RequestEphemeral(prompt ?? "", generation, callback));
    }

    public override void CancelEphemeralMsg()
    {
        m_EphemeralGeneration++;
        if (m_EphemeralRequest != null)
        {
            try { m_EphemeralRequest.Abort(); }
            catch (Exception) { }
            m_EphemeralRequest = null;
        }
    }

    private IEnumerator RequestEphemeral(string prompt, int generation, Action<string> callback)
    {
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            m_EphemeralRequest = request;
            string json = BuildEphemeralRequestJson(prompt);
            byte[] data = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(data);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader(
                "Authorization",
                string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));

            yield return request.SendWebRequest();
            if (generation != m_EphemeralGeneration) yield break;

            string responseText = "";
            if (request.responseCode == 200)
            {
                MessageBack response = JsonUtility.FromJson<MessageBack>(request.downloadHandler.text);
                if (response != null && response.choices != null && response.choices.Count > 0 &&
                    response.choices[0] != null && response.choices[0].message != null)
                {
                    responseText = response.choices[0].message.content ?? "";
                }
            }
            else if (request.result != UnityWebRequest.Result.ConnectionError && m_LogRequestStats)
            {
                Debug.LogWarning($"[临时草稿] 请求失败 code={request.responseCode}: {request.error}");
            }

            if (generation == m_EphemeralGeneration)
            {
                m_EphemeralRequest = null;
                if (callback != null) callback(responseText);
            }
        }
    }

    private string BuildEphemeralRequestJson(string prompt)
    {
        var selected = new List<SendData>();
        for (int i = 0; i < m_DataList.Count; i++)
        {
            SendData item = m_DataList[i];
            if (item != null && item.role == "system")
                selected.Add(new SendData(item.role, item.content ?? ""));
        }

        int keep = Mathf.Max(0, m_EphemeralHistoryMessages);
        int seen = 0;
        int start = m_DataList.Count;
        for (int i = m_DataList.Count - 1; i >= 0 && seen < keep; i--)
        {
            SendData item = m_DataList[i];
            if (item == null || item.role == "system") continue;
            seen++;
            start = i;
        }
        for (int i = start; i < m_DataList.Count; i++)
        {
            SendData item = m_DataList[i];
            if (item == null || item.role == "system") continue;
            selected.Add(new SendData(item.role, item.content ?? ""));
        }
        selected.Add(new SendData("user", prompt));

        var sb = new StringBuilder(2048);
        sb.Append('{');
        sb.Append("\"model\":");
        AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false");
        sb.Append(",\"max_tokens\":").Append(Mathf.Clamp(m_EphemeralMaxTokens, 48, 256));
        sb.Append(",\"temperature\":0.2");
        sb.Append(",\"messages\":[");
        for (int i = 0; i < selected.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendMessage(sb, selected[i]);
        }
        sb.Append(']');
        if (m_Backend == BackendType.Local)
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestStream(
        string _postWord,
        int generation,
        Action<string> _onDelta,
        Action<string> _onComplete)
    {
        stopwatch.Restart();

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            m_ActiveStreamRequest = request;
            string _jsonText = BuildRequestJson(stream: true);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(_jsonText);
            if (m_LogRequestStats) LogRequestStats(data.Length);
            request.uploadHandler = new UploadHandlerRaw(data);

            SSEDownloadHandler handler = new SSEDownloadHandler(delta =>
            {
                if (generation != m_StreamRequestGeneration) return;
                if (_onDelta != null) _onDelta(delta);
            });
            request.downloadHandler = handler;

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "text/event-stream");
            //本地Ollama不校验Bearer，填任意值无影响
            request.SetRequestHeader("Authorization", string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));

            yield return request.SendWebRequest();

            //Abort 会以 code=0 返回。只要 generation 已变化，这就是用户主动接管后的
            //正常过期请求：不报错、不写 assistant 历史，也不触发任何旧回调。
            if (generation != m_StreamRequestGeneration)
            {
                if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;
                yield break;
            }

            if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;

            if (request.responseCode == 200)
            {
                string full = handler.GetFullContent();
                m_DataList.Add(new SendData("assistant", full));
                if (_onComplete != null) _onComplete(full);
            }
            else
            {
                //出错时把请求体结构打出来(base64 替换成长度占位)，方便对比 server 报错
                string bodyDigest = SummarizeRequestBody(_jsonText);
                Debug.LogError("Qwen流式失败: code=" + request.responseCode
                    + " err=" + request.error
                    + " / 响应体: " + (string.IsNullOrEmpty(request.downloadHandler.text) ? "(空)" : request.downloadHandler.text)
                    + " / 请求体摘要: " + bodyDigest);
                if (_onComplete != null) _onComplete("");
            }

            stopwatch.Stop();
            Debug.Log("Qwen流式总耗时：" + stopwatch.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// base版CheckHistory每次只删一条、随后又添加user+assistant两条，历史会净增长一条。
    /// Flash模型本身很快时，这个逐轮膨胀的prefill反而会成为首token主要成本。
    /// 这里为本轮的user+assistant预留两个位置，并一次裁剪到低延迟上限。
    /// </summary>
    public override void CheckHistory()
    {
        int limit = Mathf.Max(4, m_LowLatencyHistoryLimit);
        int targetBeforeRequest = Mathf.Max(2, limit - 2);
        int nonSystemCount = 0;
        for (int i = 0; i < m_DataList.Count; i++)
        {
            if (m_DataList[i] != null && m_DataList[i].role != "system") nonSystemCount++;
        }

        int removed = 0;
        while (nonSystemCount > targetBeforeRequest)
        {
            int removeIndex = -1;
            for (int i = 0; i < m_DataList.Count; i++)
            {
                if (m_DataList[i] != null && m_DataList[i].role != "system")
                {
                    removeIndex = i;
                    break;
                }
            }
            if (removeIndex < 0) break;
            m_DataList.RemoveAt(removeIndex);
            nonSystemCount--;
            removed++;
        }

        if (removed > 0 && m_LogRequestStats)
        {
            Debug.Log($"[LLM请求] 低延迟历史裁剪: 移除{removed}条，保留{nonSystemCount}条旧消息");
        }
    }

    private void LogRequestStats(int jsonBytes)
    {
        int chars = 0;
        int images = 0;
        int systemChars = 0;
        for (int i = 0; i < m_DataList.Count; i++)
        {
            SendData message = m_DataList[i];
            if (message == null) continue;
            int length = string.IsNullOrEmpty(message.content) ? 0 : message.content.Length;
            chars += length;
            if (message.role == "system") systemChars += length;
            if (!string.IsNullOrEmpty(message.imageDataUrl)) images++;
        }
        Debug.Log($"[LLM请求] model={CurrentModelName}, messages={m_DataList.Count}, chars={chars}, systemChars={systemChars}, images={images}, json={jsonBytes / 1024f:F1}KB, thinking={m_EnableThinking}");
    }

    /// <summary>
    /// 解析 SSE 的自定义 DownloadHandler，每收到一段 data: 即解析 delta.content 并触发回调
    /// </summary>
    private class SSEDownloadHandler : DownloadHandlerScript
    {
        private Action<string> m_OnDelta;
        private StringBuilder m_LineBuf = new StringBuilder();
        private StringBuilder m_FullContent = new StringBuilder();

        public SSEDownloadHandler(Action<string> onDelta) : base(new byte[4096])
        {
            m_OnDelta = onDelta;
        }

        public string GetFullContent() { return m_FullContent.ToString(); }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return false;

            string incoming = Encoding.UTF8.GetString(data, 0, dataLength);
            m_LineBuf.Append(incoming);

            string bufStr = m_LineBuf.ToString();
            int lastNL = bufStr.LastIndexOf('\n');
            if (lastNL < 0) return true;

            string ready = bufStr.Substring(0, lastNL + 1);
            string leftover = bufStr.Substring(lastNL + 1);
            m_LineBuf.Length = 0;
            m_LineBuf.Append(leftover);

            string[] lines = ready.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:")) continue;

                string payload = line.Substring(5).Trim();
                if (payload == "[DONE]") continue;

                try
                {
                    StreamChunk chunk = JsonUtility.FromJson<StreamChunk>(payload);
                    if (chunk != null && chunk.choices != null && chunk.choices.Count > 0)
                    {
                        string delta = chunk.choices[0].delta != null ? chunk.choices[0].delta.content : null;
                        //忽略 reasoning_content (Qwen3思考过程)，只取最终答复content
                        if (!string.IsNullOrEmpty(delta))
                        {
                            m_FullContent.Append(delta);
                            if (m_OnDelta != null) m_OnDelta(delta);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("SSE解析失败: " + e.Message + " / " + payload);
                }
            }
            return true;
        }
    }

    [Serializable]
    private class StreamChunk
    {
        public List<StreamChoice> choices;
    }
    [Serializable]
    private class StreamChoice
    {
        public StreamDelta delta;
        public string finish_reason;
    }
    [Serializable]
    private class StreamDelta
    {
        public string role;
        public string content;
        public string reasoning_content;
    }

    #endregion


    #region 数据定义
    [Serializable]
    public class PostData
    {
        public string model;
        public List<SendData> messages;
        public bool stream = false;//流式
        public bool enable_thinking = false;//Qwen3思考模式
    }
    [Serializable]
    public class MessageBack
    {
        public string id;
        public string created;
        public string model;
        public List<MessageBody> choices;
    }
    [Serializable]
    public class MessageBody
    {
        public Message message;
        public string finish_reason;
        public string index;
    }
    [Serializable]
    public class Message
    {
        public string role;
        public string content;
    }

    #endregion

}
