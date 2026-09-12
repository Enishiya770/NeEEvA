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
    /// User-visible runtime failures.  Consumers render these as non-character notices;
    /// they must never be inserted into dialogue history or spoken through TTS.
    /// </summary>
    public event Action<SystemNotice> OnSystemNotice;

    /// <summary>
    /// Diagnostic copy of the model's response content before channel projection.
    /// Never use this event to dispatch speech, actions or history writes.
    /// </summary>
    public event Action<string> OnRawResponse;
    // Diagnostic only: final serialized main request; consumers must not log
    // the full payload (which may contain images) or dispatch anything from it.
    public event Action<string> OnRequestDiagnostic;
    protected void RaiseRequestDiagnostic(string json) => OnRequestDiagnostic?.Invoke(json);
    // Protocol failures are routed to the existing LLM correction path, never TTS.
    public event Action<string> OnOutputFormatError;
    protected void RaiseOutputFormatError(string reason) => OnOutputFormatError?.Invoke(reason);
    // Separate scope: invalid speech staging can coexist with a valid room motion.
    public event Action<string, string> OnSpeechPhaseError;
    protected void RaiseSpeechPhaseError(string reason, string correction) => OnSpeechPhaseError?.Invoke(reason, correction);

    protected void RaiseRawResponse(string content)
    {
        OnRawResponse?.Invoke(content ?? "");
    }

    protected void RaiseSystemNotice(SystemNotice notice)
    {
        if (notice != null && OnSystemNotice != null) OnSystemNotice(notice);
    }

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
    /// 历史消息保留条数(高水位)。仅对使用基类 CheckHistory 的 provider 生效。
    ///
    /// ChatQW 重写了 CheckHistory，改用自己的 m_LowLatencyHistoryLimit，
    /// 完全不读本字段——在 ChatQW 上调它没有任何效果。
    /// </summary>
    [Header("历史消息保留条数 (ChatQW 不使用，见下方 Tooltip)")]
    [Tooltip("超过此条数才裁剪，且一次裁到 70% 留出空位——每轮只删一条会让 system 之后的" +
             "token 序列逐轮平移，llama.cpp 的前缀缓存因此每轮只能命中 system prompt。\n\n" +
             "⚠ ChatQW 重写了 CheckHistory，用的是「低延迟模式：请求中最多保留的非system" +
             "历史消息数」那一项，本字段对它无效。改这里不会有任何变化。")]
    [SerializeField] protected int m_HistoryKeepCount = 15;
    /// <summary>
    /// 对话消息列表(运行时滚动刷新)
    /// </summary>
    [SerializeField] public List<SendData> m_DataList = new List<SendData>();

    /// <summary>
    /// 闭眼后停止重复提交旧截图像素。只归档附件，不删除当时的文字对话或改写记忆；
    /// 再睁眼时新截图仍可正常提交，旧帧不会自动复活、反复打断稳定前缀缓存。
    /// </summary>
    public int ArchiveHistoricalImagePixels()
    {
        int archived = 0;
        if (m_DataList == null) return archived;
        foreach (SendData message in m_DataList)
        {
            if (message == null || message.imageArchived || string.IsNullOrEmpty(message.imageDataUrl)) continue;
            message.imageArchived = true;
            archived++;
        }
        return archived;
    }

    /// <summary>
    /// 每次请求追加到消息列表**最末尾**的易变上下文（当前用于拓扑记忆网络的记忆块）。
    ///
    /// 它不进 m_DataList，因此不会随对话沉淀进历史。放在末尾是关键：前面的
    /// system + 历史构成稳定前缀，llama.cpp 的前缀缓存可以完整命中，只需重算这一段。
    /// 若把它拼进感知帧，帧会留在历史里，等于每轮复制一份，白白占用上下文并加速触发裁剪。
    /// </summary>
    [System.NonSerialized] public string TrailingContext = "";
    /// <summary>One-request preparation facts; consumed by the provider, never dialogue history.</summary>
    [System.NonSerialized] public string RequestContext = "";
    /// <summary>
    /// 本轮按需加载的技能提示词。和 TrailingContext 一样只在请求序列化时临时插入，
    /// 不写进 m_DataList；因此技能启停不会改写稳定的 system 前缀，也不会在历史里复制。
    /// </summary>
    [System.NonSerialized] public string ActiveSkillContext = "";
    /// <summary>
    /// 很短的常驻能力目录。它只告诉角色“有哪些 Skill 可以申请”以及当前权限状态，
    /// 不包含任何具体工具规则；详细规则仍由 SetActiveSkills 按需加载。
    /// </summary>
    [System.NonSerialized] public string SkillCatalogContext = "";
    /// <summary>
    /// 本轮已经通过快速回应出声、但还没进历史的那句开场。非空时它会作为一条
    /// **assistant 消息挂在用户消息之后**，正式回复相当于从它往下续写。
    ///
    /// 之前是把"你已经说过 X，别重复"写成散文塞进 user 消息里，模型看到的是用户
    /// 在转述它说过的话，结构上很弱：8/11 实测那一轮 hint 明写了不要重复，她照样
    /// 又说了一遍。离线对照(真系统提示词，两个真实例子各 5 次)：散文形态仍有
    /// 1/5 重说、2/5 重新打招呼，换成 assistant 消息后两项都是 0/5。
    ///
    /// 由调用方每次请求前无条件赋值(没有就赋空串)，所以不会跨轮泄漏。
    /// 收到回复时与回复合并成一条 assistant 历史——那才是用户实际听到的一整段。
    /// </summary>
    [System.NonSerialized] public string SpokenPrefix = "";
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
    /// 可按需加载的技能提示词。文件名就是技能名，例如 singing.txt 对应 singing。
    /// 它们不会进入基础 system prompt，只有调用 SetActiveSkills 后才随当前请求发送。
    /// </summary>
    [Header("按需技能Prompt(文件名即技能名，不进入基础system)")]
    [SerializeField] protected TextAsset[] m_SkillFiles;
    private readonly HashSet<string> m_MissingSkillWarnings =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> m_EditorFallbackSkillWarnings =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            sb.Append(StripEmbeddedSkillBlocks(ta.text));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Prompt 源文件里可以暂存已迁移到按需技能的旧规则，便于人工对照；标记内文本
    /// 不进入基础 system prompt。真正运行时使用的是 m_SkillFiles 中的独立技能文件。
    /// </summary>
    private static string StripEmbeddedSkillBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        const string begin = "<!-- SKILL-BEGIN:";
        const string end = "<!-- SKILL-END -->";
        int searchAt = 0;
        var sb = new StringBuilder(text.Length);
        while (searchAt < text.Length)
        {
            int blockStart = text.IndexOf(begin, searchAt, StringComparison.Ordinal);
            if (blockStart < 0)
            {
                sb.Append(text, searchAt, text.Length - searchAt);
                break;
            }
            sb.Append(text, searchAt, blockStart - searchAt);
            int blockEnd = text.IndexOf(end, blockStart + begin.Length, StringComparison.Ordinal);
            if (blockEnd < 0)
            {
                UnityEngine.Debug.LogError("[LLM技能] Prompt 中有未闭合的 SKILL-BEGIN 标记");
                //配置错误时宁可退回原始完整提示，也不能静默丢掉标记后的基础规则。
                return text;
            }
            searchAt = blockEnd + end.Length;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 设置当前请求需要的技能。每次调用都会替换上一轮技能；传空值即可卸载。
    /// 返回 false 表示至少有一个技能文件未在 Inspector 中配置。
    /// </summary>
    public virtual bool SetActiveSkills(params string[] skillNames)
    {
        bool allFound = true;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(SkillCatalogContext))
            sb.Append(SkillCatalogContext.Trim());

        if (skillNames == null || skillNames.Length == 0)
        {
            ActiveSkillContext = sb.ToString();
            return true;
        }

        for (int n = 0; n < skillNames.Length; n++)
        {
            string wanted = (skillNames[n] ?? "").Trim();
            if (wanted.Length == 0) continue;

            TextAsset match = null;
            if (m_SkillFiles != null)
            {
                for (int i = 0; i < m_SkillFiles.Length; i++)
                {
                    TextAsset candidate = m_SkillFiles[i];
                    if (candidate != null &&
                        string.Equals(candidate.name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        match = candidate;
                        break;
                    }
                }
            }

#if UNITY_EDITOR
            //私有整合场景可能是在新增 m_SkillFiles 字段之前生成的。此时场景里的数组为空，
            //但技能资产已经存在。Editor 下按规范路径后备加载，避免必须重建/重开整个场景；
            //正式 Player 仍要求场景序列化真实引用，保证打包时资产会被纳入。
            if (match == null)
            {
                string editorPath =
                    $"Assets/AIChatTookit/Prompts/Skills/{wanted}.txt";
                match = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(editorPath);
                if (match != null && m_EditorFallbackSkillWarnings.Add(wanted))
                    UnityEngine.Debug.LogWarning(
                        $"[LLM技能] 当前场景未序列化 '{wanted}'，已从 {editorPath} 后备加载。" +
                        "重新生成或保存该场景后可消除此警告。");
            }
#endif

            if (match == null || string.IsNullOrWhiteSpace(match.text))
            {
                allFound = false;
                if (m_MissingSkillWarnings.Add(wanted))
                    UnityEngine.Debug.LogError(
                        $"[LLM技能] 找不到技能 '{wanted}'。请把 {wanted}.txt 加到 Skill Files。");
                continue;
            }

            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("[已加载技能: ").Append(wanted).Append("]\n");
            sb.Append(match.text.Trim());
        }
        ActiveSkillContext = sb.ToString();
        return allFound;
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
        string imageDataUrl = null,
        bool recordAssistantHistory = true)
    {
        PostMsg(_msg, (full) =>
        {
            if (_onDelta != null) _onDelta(full);
            if (_onComplete != null) _onComplete(full);
        });
    }

    /// <summary>
    /// 在最后一条真实 user 消息上继续生成，不追加一条伪造的 user 消息。
    /// transientSystemContext 只进入这一次请求，不写进历史。用于异步工具已经返回、
    /// 而用户仍在等待同一轮最终答复的场景。默认 provider 退回普通流式消息；支持
    /// 原生历史控制的 provider 应覆写，保持一个真实 user 对应一个最终 assistant。
    /// </summary>
    public virtual void PostContinuationStream(
        string transientSystemContext,
        System.Action<string> _onDelta,
        System.Action<string> _onComplete,
        string imageDataUrl = null)
    {
        PostMsgStream(
            transientSystemContext ?? "",
            _onDelta,
            _onComplete,
            imageDataUrl,
            true);
    }

    /// <summary>Typed speech path. Each invocation owns its parser and language state.</summary>
    public virtual void PostSpeechMessage(string message, Action<List<SpeechText>, string> onComplete)
    {
        var buffer = new SpeechTextBuffer();
        PostSpeechStream(message, part => buffer.Append(part),
            full => onComplete?.Invoke(buffer.Snapshot(), full));
    }

    public virtual void PostSpeechStream(string message, Action<SpeechText> onSpeech,
        Action<string> onComplete, string imageDataUrl = null, bool recordAssistantHistory = true)
    {
        var channels = new RoleOutputChannels(onSpeech);
        PostMsgStream(message, delta => channels.Push(delta), full =>
        {
            channels.Finish();
            onComplete?.Invoke(string.IsNullOrWhiteSpace(full) ? "" : RoleOutputChannels.Parse(full).ToExecutableText());
        }, imageDataUrl, recordAssistantHistory);
    }

    public virtual void PostSpeechContinuationStream(string context, Action<SpeechText> onSpeech,
        Action<string> onComplete, string imageDataUrl = null)
    {
        var channels = new RoleOutputChannels(onSpeech);
        PostContinuationStream(context, delta => channels.Push(delta), full =>
        {
            channels.Finish();
            onComplete?.Invoke(string.IsNullOrWhiteSpace(full) ? "" : RoleOutputChannels.Parse(full).ToExecutableText());
        }, imageDataUrl);
    }

    /// <summary>Continue after a completed assistant action with new execution facts.
    /// Providers with native history control place feedback after that action's reply.
    /// Other providers retain their existing continuation transport.</summary>
    public virtual void PostSpeechFeedbackStream(string context, string feedback, Action<SpeechText> onSpeech,
        Action<string> onComplete, string imageDataUrl = null)
    {
        PostSpeechContinuationStream((context ?? "") + "\n\n" + (feedback ?? ""), onSpeech, onComplete, imageDataUrl);
    }

    /// <summary>
    /// 临时推理：给“用户仍在说话”的可撤销草稿使用。
    /// 实现必须保证请求和回答都不写入 m_DataList；不支持的 provider 返回空结果。
    /// </summary>
    public virtual void PostEphemeralMsg(string prompt, System.Action<string> callback)
    {
        if (callback != null) callback("");
    }

    /// <summary>
    /// Review outstanding user work without writing dialogue history. Providers may use
    /// a separate structured-output budget; CancelEphemeralMsg also cancels this review.
    /// The fallback preserves existing providers and test doubles.
    /// </summary>
    public virtual void PostWorkReviewMsg(string prompt, System.Action<string> callback)
    {
        PostEphemeralMsg(prompt, callback);
    }

    /// <summary>用户继续说或 EOU 到达时撤销在飞的临时推理，正式回复拥有最高优先级。</summary>
    public virtual void CancelEphemeralMsg()
    {
    }

    /// <summary>
    /// 持续声响下的轮次边界复核。它与普通投机草稿分开管理：边界判断优先级更高，
    /// 不能被歌唱预反应的逐帧刷新取消或饿死；同样不得写入正式对话历史。
    /// </summary>
    public virtual bool SupportsTurnBoundaryMessages { get { return false; } }

    public virtual void PostTurnBoundaryMsg(
        string prompt,
        System.Action<string> callback)
    {
        if (callback != null) callback("");
    }

    public virtual void CancelTurnBoundaryMsg()
    {
    }

    /// <summary>
    /// Stateless utility inference with a caller-provided minimal system prompt.  It is
    /// intentionally separate from PostMsg/PostEphemeralMsg so translation cannot enter,
    /// cancel, or inherit the character conversation.  Providers opt in explicitly.
    /// </summary>
    public virtual bool SupportsUtilityMessages { get { return false; } }

    public virtual void PostUtilityMessage(
        string systemPrompt,
        string input,
        Action<bool, string, string> callback)
    {
        if (callback != null) callback(false, "", "provider does not support utility messages");
    }

    public virtual void CancelUtilityMessage()
    {
    }

    /// <summary>Same-model, request-local spatial decision and speech consistency check. No history or side effects.</summary>
    public virtual bool SupportsRoomTaskMessages => false;
    public virtual void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback)
        => callback?.Invoke(false, "", "room-task-provider-unavailable");

    /// <summary>
    /// 撤销当前正式回复。用户重新开口或打断角色时调用。
    /// 子类应同时停止网络请求，并保证过期回调不再写入历史或触发 TTS。
    /// </summary>
    public virtual void CancelActiveResponse()
    {
    }

    /// <summary>
    /// 维护历史消息条数，避免上下文过长。
    ///
    /// 到上限后一次腾出一批，而不是每轮删一条。原先每轮删一条会让 system 之后的
    /// token 序列每轮整体平移，llama.cpp 的前缀缓存于是每轮只能命中 system prompt
    /// 本身，其后数千 token 全部重算——实测固定命中 7356 token、每轮重算约 5000
    /// token，光 prompt 处理就多花 5 秒多。批量腾挪后，缓存只在腾挪的那一轮失效，
    /// 中间几轮都是纯追加，可以完整命中。
    /// </summary>
    public virtual void CheckHistory()
    {
        if (m_DataList.Count <= m_HistoryKeepCount) return;

        //跳过system消息(人设)，从第一条非system消息开始删，避免删掉人设导致角色失忆
        int startIdx = (m_DataList.Count > 0 && m_DataList[0] != null && m_DataList[0].role == "system") ? 1 : 0;
        int capacity = Mathf.Max(1, m_HistoryKeepCount - startIdx);
        //留出约三成空位，够接下来几轮纯追加
        int target = startIdx + Mathf.Max(1, Mathf.RoundToInt(capacity * 0.7f));
        int removeCount = m_DataList.Count - target;
        if (removeCount <= 0) return;
        //成对删除，避免历史以 assistant 开头
        if (removeCount % 2 != 0) removeCount++;
        removeCount = Mathf.Min(removeCount, m_DataList.Count - startIdx);
        if (removeCount > 0)
            m_DataList.RemoveRange(startIdx, removeCount);
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
        [NonSerialized] public bool imageArchived;
        public SendData() { }
        public SendData(string _role, string _content)
        {
            role = _role;
            content = _content;
        }

    }

}
