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

    // llama-server 的两个槽位各自持有一份 KV 缓存。--slot-prompt-similarity 0.8 本该
    // 把请求匹配到"前缀最像"的槽，但投机草稿的 prompt 就是主对话的前缀、相似度极高，
    // 它一更新那个槽，主对话的前缀就没了；下一轮主对话被分到另一个槽，整段重算。
    //
    // 8/11 实测（llama 自己的 slot 日志 + Unity 首 token）：
    //   112 个主对话请求里 4 个复用率 0%，其中 3 个不是会话首个
    //   命中时 prefill 0.47s（约 1050 tok/s）→ 首音 0.6~1.5s
    //   没命中时整段重算 9451~10114 token → prefill 9.0~9.5s → 首音 9.64s
    //   那 3 次就是"首音超 2 秒"的全部来源
    //
    // 所以两条流各钉一个槽，谁也别碰对方的缓存：
    //   槽 0 = 主对话 + 预热（预热的 prompt 与主对话同前缀，本来就该暖同一个槽。
    //          旧注释记着"预热暖的是 slot 1、首个正式请求被 LRU 分到 slot 0、
    //          重算 9792 token(14.40s)"——钉槽把这个浪费一起修掉）
    //   槽 1 = 投机草稿 + 模态判定 + 撤回判定（都不在首音路径上，被挤掉也不心疼）
    //
    // 只对本地后端有效；DashScope 不认这个字段。
    private const int k_SlotMainConversation = 0;
    private const int k_SlotAuxiliary = 1;

    private void AppendSlot(StringBuilder sb, int slot)
    {
        if (m_Backend != BackendType.Local) return;
        sb.Append(",\"id_slot\":").Append(slot);
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
        // 易变上下文(记忆块)排在**最后一条用户消息之前**，而不是整个列表末尾。
        //
        // 曾经拼在末尾，结果是她读到的最后一段不是用户的话而是记忆块——而记忆块末尾的
        // 气泡段落正是用第三人称复述用户刚说过的话。实测一整场里冒出 6 句
        // "彼、日本語なら気楽に話せるようだね" 这类第三人称反思，全部落在回复末尾的
        // 标签位、并且被当成正文念了出来(同场 <silent/> 使用次数从 3 跌到 0)。
        // 把用户的话放回最后，这个诱因就没了。
        //
        // 顺带修正了一处不一致：PostEphemeralMsg 构造草稿时本来就是
        // [system][历史][记忆块][user]，与这里的顺序相反，注释却写着"必须一致"。
        // 现在两者真正一致，草稿 prompt 重新成为主 prompt 的严格前缀。
        //
        // 缓存行为不变：记忆块每轮都变，重算量仍然是"记忆块 + 用户这一句"。
        int trailingAt = -1;
        if (!string.IsNullOrEmpty(TrailingContext))
        {
            trailingAt = m_DataList.Count;   //没有 user 消息时退回原来的"拼在末尾"
            for (int i = m_DataList.Count - 1; i >= 0; i--)
            {
                var m = m_DataList[i];
                if (m != null && m.role == "user") { trailingAt = i; break; }
            }
        }
        for (int i = 0; i < m_DataList.Count; i++)
        {
            if (i == trailingAt)
            {
                if (sb[sb.Length - 1] != '[') sb.Append(',');
                AppendMessage(sb, new SendData("system", TrailingContext));
            }
            var msg = m_DataList[i];
            if (msg == null) continue;
            if (sb[sb.Length - 1] != '[') sb.Append(',');
            AppendMessage(sb, msg);
        }
        if (trailingAt >= m_DataList.Count)
        {
            if (sb[sb.Length - 1] != '[') sb.Append(',');
            AppendMessage(sb, new SendData("system", TrailingContext));
        }
        //已经出声的开场排在最后：用户的话仍是最后一条 user，它跟在后面，
        //本轮回复于是变成"接着这句往下说"而不是"另写一段"。不要在这里再补一条
        //解释它的 system——离线实测加了之后 #8 那例 5/5 全塌成 <silent/>，
        //位置本身就够了。
        if (!string.IsNullOrWhiteSpace(SpokenPrefix))
        {
            if (sb[sb.Length - 1] != '[') sb.Append(',');
            AppendMessage(sb, new SendData("assistant", SpokenPrefix));
        }
        sb.Append(']');
        if (m_Backend == BackendType.Local)
        {
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":")
              .Append(m_EnableThinking ? "true" : "false")
              .Append('}');
        }
        AppendSlot(sb, k_SlotMainConversation);
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

    [Header("历史消息高水位 (ChatQW 实际使用的就是这一项)")]
    [Tooltip("非system消息超过此条数才裁剪，且一次裁到 25%(低水位)，中间若干轮都是纯追加。\n\n" +
             "不要按「保留多少轮对话」来理解它：原先每轮裁到固定条数，等于每轮删最老两条，" +
             "前缀逐轮平移，llama.cpp 的缓存每轮只能命中 system prompt、其余数千 token 全部" +
             "重算(实测每轮多 5 秒)。高低水位让缓存只在腾挪那一轮失效。\n\n" +
             "平均每轮重算量 = 2×target/(limit-target)，按固定比例取 target 时与 limit 无关，" +
             "所以单纯抬高上限没有收益——要压的是比值。当前 32/8 => 0.67。\n\n" +
             "上限受 llama-server 每槽 ctx 约束：主对话上下文峰值实测 18389 token。\n" +
             "注意：基类那个「历史消息保留条数」对 ChatQW 无效。")]
    [Range(4, 64)] public int m_LowLatencyHistoryLimit = 32;

    [Header("Debug：打印LLM请求大小/消息数（不打印正文和密钥）")]
    public bool m_LogRequestStats = true;

    [Header("流式倾听临时草稿")]
    [Tooltip("临时请求的最大输出 token。它不进入历史，并会在 EOU 时被撤销。")]
    [Range(48, 256)] public int m_EphemeralMaxTokens = 128;
    [Tooltip("临时请求携带多少条最近的非 system 消息。\n" +
             "0 = 携带完整历史(推荐)：草稿 prompt 因此成为主对话 prompt 的严格延长，" +
             "两者共享同一段长前缀，llama.cpp 只需重算末尾的指令部分。\n" +
             "取正数会从历史中间截取一段，token 序列与主对话对不上，只能共享 system " +
             "提示词——实测每轮要多重算约 2800 token(约 2.5 秒)，且草稿记得更少。")]
    [Range(0, 8)] public int m_EphemeralHistoryMessages = 0;

    [Header("开场预热")]
    [Tooltip("场景启动时发一发空请求，把 7000+ token 的系统提示词提前灌进 llama.cpp 的 " +
             "KV 缓存。实测会话第一轮首 token 要 5-8 秒(前缀全冷)，暖机后同样的轮次只需 " +
             "0.7-2.1 秒。这一项把那笔一次性开销挪到用户开口之前，不产生任何可见输出。")]
    public bool m_PrewarmPrefixOnStart = true;

    private UnityWebRequest m_EphemeralRequest;
    private int m_EphemeralGeneration = 0;
    private UnityWebRequest m_PrewarmRequest;

    /// <summary>
    /// 预热还在飞就立刻放弃它。场景启动时各服务都在抢资源，实测预热要 12 秒才回来
    /// (单独测只要 2 秒)；用户若在这期间开口，真实请求会排在预热后面，首轮反而更慢
    /// ——实测首 token 被拖到 10.48s。
    ///
    /// 注意：这里放弃的只是**等待**，服务端不会跟着停。8/8 实测被 Abort 的预热
    /// 照样跑完并打出完整计时(prompt eval 19865.85ms / 8927 token @449 tok/s)，
    /// 随后正常 release。所以"抢占是干净的"只对客户端成立，那笔 GPU 时间照付。
    ///
    /// 也别指望首轮能白捡这份前缀：--parallel 2 有两个槽位，预热暖的是 slot 1，
    /// 同一场的首个正式请求被 LRU 分到空着的 slot 0，复用 0、重算 9792 token
    /// (14.40s)。不过这笔账按 llama-server 的生命周期算，不按 Play 算——服务不重启
    /// 时第二次 Play 的预热只要 3.14s、首轮首 token 1.47s，所以不值得为它改开场逻辑。
    /// </summary>
    private void AbortPrewarmIfRunning()
    {
        if (m_PrewarmRequest == null) return;
        try { m_PrewarmRequest.Abort(); } catch (Exception) { }
        m_PrewarmRequest = null;
        if (m_LogRequestStats) Debug.Log("[LLM预热] 用户已开口，放弃预热");
    }

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

        if (m_PrewarmPrefixOnStart) StartCoroutine(PrewarmPrefix());
    }

    /// <summary>
    /// 开场先把系统提示词灌进 llama.cpp 的 KV 缓存。
    ///
    /// 系统提示词有 7000+ token，会话第一轮必须现算，实测首 token 要 5-8 秒；暖机之后
    /// 同样的轮次只要 0.7-2.1 秒。这一发空请求把那笔一次性开销挪到用户开口之前。
    /// 只发 [system] + 一个极短的 user，max_tokens=1，不写进 m_DataList、不触发任何回调。
    /// </summary>
    private IEnumerator PrewarmPrefix()
    {
        //等一帧，确保 system 消息已经装好
        yield return null;
        string sys = null;
        for (int i = 0; i < m_DataList.Count; i++)
            if (m_DataList[i] != null && m_DataList[i].role == "system") { sys = m_DataList[i].content; break; }
        if (string.IsNullOrEmpty(sys)) yield break;

        var sb = new StringBuilder(sys.Length + 256);
        sb.Append('{');
        sb.Append("\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false");
        sb.Append(",\"max_tokens\":1,\"temperature\":0");
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", sys));
        sb.Append(',');
        AppendMessage(sb, new SendData("user", "."));
        sb.Append(']');
        if (m_Backend == BackendType.Local)
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotMainConversation);   // PrewarmPrefix 预热(与主对话同槽)
        sb.Append('}');

        float t0 = Time.realtimeSinceStartup;
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            m_PrewarmRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader(
                "Authorization",
                string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            yield return request.SendWebRequest();
            bool aborted = m_PrewarmRequest == null;   //被真实请求抢占
            m_PrewarmRequest = null;

            if (m_LogRequestStats && !aborted)
            {
                float dt = Time.realtimeSinceStartup - t0;
                if (request.responseCode == 200)
                    Debug.Log($"[LLM预热] 系统提示词前缀已入缓存 ({sys.Length} 字符, {dt:F2}s)");
                else
                    Debug.LogWarning($"[LLM预热] 失败 code={request.responseCode}: {request.error}");
            }
        }
    }

    /// <summary>
    /// 判断一段最终转写是唱歌还是说话，回调返回 "singing" / "speech" / ""(判不出)。
    ///
    /// 刻意不带会话上下文：实测同样的判断，挂在 12k token 的完整上下文后面要 2.64s
    /// (其中生成整个 JSON 占 2.2s、上下文预填充另占约 1.6s)，而只带这一句转写、
    /// 只要一个词时是 **0.41s**，十个历史误判样本全部判对。
    ///
    /// 必须关思考：模型是 thinking 型的，不关的话 content 一直为空，答案卡在
    /// reasoning_content 里出不来。
    /// </summary>
    /// <param name="segmentLyrics">
    /// 声学上被判为演唱的那一段的**单独转写**。整轮 ASR 按整轮语言跑，唱的那段语言
    /// 不同时会被整段丢掉：8/9 实测三轮连续的「中文说话 + 日文演唱」，整轮转写里
    /// 一个假名都没有(全日志搜 夢/でしょう 只在唯一转对的那轮出现过)，而片段转写
    /// 拿到了 31~32 字 lang=ja。分类器只看整轮转写时只能答 speech，三段真演唱因此
    /// 被软降级丢弃，用户当场反馈「你还是没唱出来啊」。
    /// </param>
    public void ClassifyUtteranceMode(
        string transcript, Action<string> callback, string segmentLyrics = null)
    {
        if (callback == null) return;
        if (string.IsNullOrWhiteSpace(transcript)) { callback(""); return; }
        StartCoroutine(ClassifyUtteranceModeRoutine(
            transcript.Trim(),
            string.IsNullOrWhiteSpace(segmentLyrics) ? "" : segmentLyrics.Trim(),
            callback));
    }

    /// <summary>
    /// 回哼转换要跑十几秒，这期间她一个字都不说。让她自己决定要不要垫一句、垫什么。
    /// 回调给出要说的话；判断"这会儿没什么值得说的"时给空串。
    ///
    /// 与转换**并行**跑，藏在那十几秒里，不占首音延迟。
    /// 刻意不写死模板：项目里已有的 TryPlayLatencyFiller 用的是预缓存固定音频，
    /// 那套在这里会很快听腻——每次都该结合当下(唱的什么语言、第几遍、刚才聊到哪)。
    /// </summary>
    public void ComposeHumBackPrelude(
        string recentLyrics, string turnContext, string recentPreludes,
        Action<string> callback)
    {
        if (callback == null) return;
        StartCoroutine(ComposeHumBackPreludeRoutine(
            (recentLyrics ?? "").Trim(), (turnContext ?? "").Trim(),
            (recentPreludes ?? "").Trim(), callback));
    }

    private IEnumerator ComposeHumBackPreludeRoutine(
        string recentLyrics, string turnContext, string recentPreludes,
        Action<string> callback)
    {
        // 措辞是量出来的，这个旋钮极其敏感——同一批 5 段素材上跑：
        //   只说"有想说的就说" .............. 沉默 40%，但 3/9 句把角色搞反或冒系统腔
        //   重写成带三条禁令的结构 .......... 沉默 7%，句子干净（几乎每次都说，等于机械）
        //   再强调"大多数时候安静就好" ...... 沉默 67%，但开口的那些变懒、开始复述歌词
        //   再加一句"沉默不需要理由" ........ 沉默 100%，一句都不说了
        //   回到第一版原样、只补三条具体禁令 . 沉默 55%，问题句 0  ← 就是下面这版
        // 教训：坏的只是几个具体毛病时，别重写框架，补最小的禁令就够。
        var prompt = new StringBuilder(640);
        prompt.Append(
            "他刚唱完一段，接下来轮到你用自己的声音把它唱回来，这期间有十几秒是安静的。\n" +
            "如果此刻有什么自然想说的——对刚才那段的感觉、一句随口的评价——就说出来，" +
            "一到两句，口语，别太正式。\n" +
            "**如果这会儿没什么值得说的，就只回一个短横线 -**。宁可不说，也不要硬凑。\n" +
            "要唱的人是你不是他，别说「期待你的版本」这类把两人搞反的话；" +
            "不要提转换、合成、加载、稍等这类幕后过程；不要把歌词原样复述一遍；" +
            "也不要预告你待会儿会唱成什么样。\n");
        if (!string.IsNullOrEmpty(recentLyrics))
            prompt.Append("\n他刚才唱的是：").Append(recentLyrics);
        if (!string.IsNullOrEmpty(turnContext))
            prompt.Append("\n这一轮他还说了：").Append(turnContext);
        // 这是个裸请求、不带会话历史，她不知道自己上次垫了什么——实测同一段素材
        // 连着三次都说「这调子挺抓耳的」。把最近几句带进来才不会重复。
        if (!string.IsNullOrEmpty(recentPreludes))
            prompt.Append("\n\n你最近在这种时候说过：").Append(recentPreludes)
                  .Append("\n换个说法，别重复上面这些。");

        var sb = new StringBuilder(prompt.Length + 256);
        sb.Append('{');
        sb.Append("\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false");
        sb.Append(",\"max_tokens\":80,\"temperature\":0.9");
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("user", prompt.ToString()));
        sb.Append(']');
        if (m_Backend == BackendType.Local)
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary);
        sb.Append('}');

        float t0 = Time.realtimeSinceStartup;
        string line = "";
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader(
                "Authorization",
                string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            request.timeout = 10;
            yield return request.SendWebRequest();
            if (request.responseCode == 200)
            {
                try
                {
                    MessageBack back = JsonUtility.FromJson<MessageBack>(
                        request.downloadHandler.text);
                    if (back != null && back.choices != null && back.choices.Count > 0 &&
                        back.choices[0] != null && back.choices[0].message != null)
                        line = back.choices[0].message.content ?? "";
                }
                catch (Exception) { line = ""; }
            }
            line = (line ?? "").Trim();
            //她选择不说时会回一个短横线；标签/系统腔也一律当作不说
            if (line == "-" || line == "—" || line.StartsWith("-") || line.Contains("<"))
                line = "";
            if (m_LogRequestStats)
                Debug.Log($"[回哼垫场] {(line.Length == 0 ? "她选择不说" : "\"" + line + "\"")} " +
                          $"用时 {Time.realtimeSinceStartup - t0:F2}s code={request.responseCode}");
        }
        callback(line);
    }

    /// <summary>
    /// 用户唱完之后那句话，是不是在把刚才那段演唱作废。
    /// 回调 true 只在模型明确说作废时给出；判不出、请求失败、文本为空一律 false。
    ///
    /// 调用方应当让它与歌声转换**并行**跑：转换要十几秒，这个判定不到一秒，
    /// 完全藏得住，所以不占首音延迟。判 true 时中止转换即可。
    ///
    /// 宁可漏判也不能误判：漏了最多多唱一次(用户开口就会触发 barge-in 中止)，
    /// 误判则是静默吞掉一段本该回哼的演唱，用户看不到任何反馈。
    /// </summary>
    public void ClassifySingingRetraction(string tailText, Action<bool> callback)
    {
        if (callback == null) return;
        if (string.IsNullOrWhiteSpace(tailText)) { callback(false); return; }
        StartCoroutine(ClassifySingingRetractionRoutine(tailText.Trim(), callback));
    }

    private IEnumerator ClassifySingingRetractionRoutine(
        string tailText, Action<bool> callback)
    {
        //措辞是量出来的。第一版只说"是不是在作废"，12 个样本里 10/12，**两个错都是
        //假阳**——「这首歌叫演员，是薛之谦的。」和「那我们下一首换一个吧。」都被判成
        //作废。假阳正是不能出的方向：它会静默吞掉一段本该回哼的演唱。
        //补上反例清单、并把"拿不准答 keep"写死之后 12/12，假阳 0。
        string prompt =
            "用户刚唱完一段，紧接着说了下面这句话。判断他是不是在说**刚才唱的这一遍作废、" +
            "不要用**。\n" +
            "只有当他明确否定刚才那一遍（唱错了／不算／重来／重新唱一次）时 → discard\n" +
            "其余一律 → keep。包括：评价刚才唱得怎么样、介绍这首歌、让你跟着唱、" +
            "提议下一首换别的歌——这些都不是作废。\n" +
            "拿不准就答 keep。只回答一个词。\n\n" + tailText;

        var sb = new StringBuilder(prompt.Length + 256);
        sb.Append('{');
        sb.Append("\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false");
        sb.Append(",\"max_tokens\":8,\"temperature\":0");
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("user", prompt));
        sb.Append(']');
        if (m_Backend == BackendType.Local)
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary);   // ClassifySingingRetractionRoutine 撤回判定
        sb.Append('}');

        float t0 = Time.realtimeSinceStartup;
        bool discard = false;
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader(
                "Authorization",
                string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            request.timeout = 8;
            yield return request.SendWebRequest();
            if (request.responseCode == 200)
            {
                string body = request.downloadHandler.text ?? "";
                //只认明确的 discard；含糊一律当作没作废
                discard = body.IndexOf("discard", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (m_LogRequestStats)
                Debug.Log($"[唱完撤回判定] {(discard ? "作废" : "保留")} " +
                          $"用时 {Time.realtimeSinceStartup - t0:F2}s " +
                          $"code={request.responseCode}: \"{tailText}\"");
        }
        callback(discard);
    }

    /// <summary>
    /// 片段转写是否已经被整轮转写涵盖。按去重字符的重合率算：日文演唱那种整轮完全
    /// 漏掉的场合重合率接近 0，中文里片段本就是整轮子串的场合接近 1。
    /// </summary>
    private static bool TranscriptCoversSegment(string transcript, string segment)
    {
        if (string.IsNullOrEmpty(segment)) return true;
        var distinct = new HashSet<char>();
        foreach (char c in segment)
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c)) distinct.Add(c);
        if (distinct.Count == 0) return true;
        int covered = 0;
        foreach (char c in distinct)
            if (transcript.IndexOf(c) >= 0) covered++;
        return covered >= distinct.Count * 0.6f;
    }

    private IEnumerator ClassifyUtteranceModeRoutine(
        string transcript, string segmentLyrics, Action<string> callback)
    {
        //问法很关键：早先问"这句是 singing 还是 speech"，混合轮(说话开头+后面唱)
        //全部答 speech——句子以对话开头、又含对话内容，模型答得没错，是问题问错了。
        //流水线要的是"这一轮里有没有值得回哼的演唱"，混合轮答案应该是 singing。
        //改成下面这个问法后，同一批 20 个真实样本从 11/20 提到 17/20。
        //片段转写只在整轮转写没收录它时才附上。整轮已经包含同样的字时重复贴一遍，
        //等于把歌词在提示里说两遍，会把模型往 singing 推——而文字否决权是有用的
        //(实测「我刚才已经唱出来了呀，就是唱。」正是靠它降级的)，不能削弱。
        bool segmentAddsEvidence = segmentLyrics.Length > 0 &&
            !TranscriptCoversSegment(transcript, segmentLyrics);
        string prompt =
            "用户刚说完一段话，下面是它的转写。判断这段里**有没有真正唱出来的部分**" +
            "（哪怕前面几句是普通说话、哪怕只唱了一句）。\n" +
            "含有歌词或旋律 → singing\n" +
            "全程都是在对你讲话、提问、评论、或者只是在商量要唱什么 → speech\n" +
            "只回答一个词。\n\n" + transcript;
        if (segmentAddsEvidence)
        {
            //措辞是量出来的，不是随手写的。12 个样本(7 个来自 8/9 实测 + 5 个反面构造)
            //上扫了四种写法：只中立地说"整轮漏了这段"是 9/12——救回了三轮日文演唱，
            //但把「噪音碎片」「点播 Lemon」「引用歌词讨论」三个说话轮全推成了 singing，
            //等于废掉文字否决权。补上下面这句"引用不算唱"后是 11/12。
            //唯一剩下的误判是"边说边引用歌词"，那是文字上本来就分不开的老问题
            //(见 ChatSample 交集那段注释)，由声学模糊带的全票否决兜底，不归这里管。
            prompt += "\n\n（补充：上面的整轮转写可能漏掉了一段声音，那段单独转写是：）\n" +
                      segmentLyrics +
                      "\n\n注意：只是在谈论、引用、点播一首歌，不算唱；要真的把它唱出来才算。";
        }

        var sb = new StringBuilder(prompt.Length + 256);
        sb.Append('{');
        sb.Append("\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false");
        sb.Append(",\"max_tokens\":8,\"temperature\":0");
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("user", prompt));
        sb.Append(']');
        if (m_Backend == BackendType.Local)
            sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary);   // ClassifyUtteranceModeRoutine 模态判定
        sb.Append('}');

        float t0 = Time.realtimeSinceStartup;
        string verdict = "";
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader(
                "Authorization",
                string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            request.timeout = 8;
            yield return request.SendWebRequest();
            if (request.responseCode == 200)
            {
                string body = request.downloadHandler.text ?? "";
                //判不出时宁可返回空，让调用方沿用原有的声学结论，不要瞎猜
                if (body.IndexOf("singing", StringComparison.OrdinalIgnoreCase) >= 0)
                    verdict = "singing";
                else if (body.IndexOf("speech", StringComparison.OrdinalIgnoreCase) >= 0)
                    verdict = "speech";
            }
            if (m_LogRequestStats)
            {
                Debug.Log($"[模态判定] {(string.IsNullOrEmpty(verdict) ? "无结论" : verdict)} " +
                          $"用时 {Time.realtimeSinceStartup - t0:F2}s code={request.responseCode} " +
                          $"附片段歌词={(segmentAddsEvidence ? "是" : segmentLyrics.Length > 0 ? "否(整轮已含)" : "否(无)")}: " +
                          $"\"{(transcript.Length > 40 ? transcript.Substring(0, 40) : transcript)}\"" +
                          (segmentAddsEvidence ? $" + \"{segmentLyrics}\"" : ""));
            }
        }
        callback(verdict);
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    /// <returns></returns>
    public override void PostMsg(string _msg, Action<string> _callback)
    {
        AbortPrewarmIfRunning();
        CancelEphemeralMsg();
        base.PostMsg(_msg, _callback);
    }

    // 开头那段空 think 块的过滤状态：
    //   0=还在判断开头  1=正在吞 think 块  3=吞完了正在跳过其后的空白  2=直通。
    // 状态 3 不能省：线上 delta 就是 "<think>" / "\n\n" / "</think>" / "\n\n\n" / "確かに"
    // 这样切的，闭合标签自成一段时余量为空，若直接转直通，后面那段换行会原样漏出去。
    private int m_ThinkStripState = 0;
    private readonly StringBuilder m_ThinkStripBuffer = new StringBuilder();
    private const string k_ThinkOpen = "<think>";
    private const string k_ThinkClose = "</think>";

    private void ResetThinkStrip()
    {
        m_ThinkStripState = 0;
        m_ThinkStripBuffer.Length = 0;
    }

    /// <summary>
    /// 吞掉回复开头的 <c>&lt;think&gt;…&lt;/think&gt;</c>。
    ///
    /// Qwen3 的模板是靠**把空 think 块预先写进生成提示**来实现 enable_thinking=false 的；
    /// 消息列表最后一条是 assistant 时(SpokenPrefix 走的就是这条路)模板换了分支不再注入，
    /// 模型于是自己把这两个标签当正文写出来。8/12 实测 4 轮注入前缀、4 轮全中，
    /// 而且被当台词念了出去(各 0.6s 音频)，还进了历史的「你最近发言」。
    ///
    /// 顺带治好了抢话：标签被切成极短的块，TTS 秒回，首音从中位 5.9s 提前到 1.9s，
    /// 正好落在预合成开场还没播完的时候，把它拦腰截断。
    ///
    /// 只吞开头这一处，且必须逐段判断——delta 可能把 "&lt;think&gt;" 拆开送。
    /// 开头不是它就立刻转直通，把攒下的原样吐出去(<c>&lt;silent/&gt;</c> 这类前缀不能丢)。
    /// </summary>
    private string StripLeadingThinkBlock(string delta)
    {
        if (m_ThinkStripState == 2) return delta ?? "";
        if (m_ThinkStripState == 3)
        {
            string skipped = (delta ?? "").TrimStart('\r', '\n', ' ', '　');
            if (skipped.Length == 0) return "";
            m_ThinkStripState = 2;
            return skipped;
        }
        m_ThinkStripBuffer.Append(delta ?? "");
        string acc = m_ThinkStripBuffer.ToString();
        if (m_ThinkStripState == 0)
        {
            string head = acc.TrimStart();
            if (head.Length == 0) return "";                 //目前只有空白，继续等
            if (!head.StartsWith(k_ThinkOpen, StringComparison.Ordinal))
            {
                //还可能是被拆开的 "<thi"，那就继续等；否则确定不是，转直通
                if (k_ThinkOpen.StartsWith(head, StringComparison.Ordinal)) return "";
                m_ThinkStripState = 2;
                m_ThinkStripBuffer.Length = 0;
                return acc;
            }
            m_ThinkStripState = 1;
        }
        int close = acc.IndexOf(k_ThinkClose, StringComparison.Ordinal);
        if (close < 0) return "";
        m_ThinkStripBuffer.Length = 0;
        string rest = acc.Substring(close + k_ThinkClose.Length)
                         .TrimStart('\r', '\n', ' ', '　');
        if (rest.Length == 0) { m_ThinkStripState = 3; return ""; }
        m_ThinkStripState = 2;
        return rest;
    }

    /// <summary>
    /// 整段文本版本，用于历史与非流式回复。与流式过滤器同一条规则。
    /// </summary>
    private static string StripLeadingThinkBlock(string text, bool wholeText)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string head = text.TrimStart();
        if (!head.StartsWith(k_ThinkOpen, StringComparison.Ordinal)) return text;
        int close = head.IndexOf(k_ThinkClose, StringComparison.Ordinal);
        if (close < 0) return text;
        return head.Substring(close + k_ThinkClose.Length)
                   .TrimStart('\r', '\n', ' ', '　');
    }

    /// <summary>
    /// 把已经出声的开场和正式回复并成一条 assistant 历史，并清掉 SpokenPrefix。
    /// 历史里留的必须是用户实际听到的那一整段：拆成两条 assistant 会让后面的轮次
    /// 读到一段本不存在的对话结构，只留回复又会漏掉她真的说过的那句。
    /// </summary>
    private string MergeSpokenPrefix(string reply)
    {
        string prefix = SpokenPrefix;
        SpokenPrefix = "";
        if (string.IsNullOrWhiteSpace(prefix)) return reply ?? "";
        if (string.IsNullOrWhiteSpace(reply)) return prefix;
        return prefix.TrimEnd() + "\n" + reply.TrimStart();
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

                    string _backMsg = StripLeadingThinkBlock(
                        _textback.choices[0].message.content, true);
                    //添加记录
                    m_DataList.Add(new SendData("assistant", MergeSpokenPrefix(_backMsg)));
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
        //Agent loop 的正式回复走这条流式路径，所以抢占预热必须放在这里——只加在
        //PostMsg/PostEphemeralMsg 上会漏掉它，实测首轮排在 14.82s 的预热后面，
        //首 token 被拖到 11.76s。
        AbortPrewarmIfRunning();
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
        AbortPrewarmIfRunning();
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

        // keep <= 0：携带完整历史。草稿 prompt 于是成为主对话 prompt 的严格延长，
        // 两者共享同一段长前缀，KV 缓存对双方都几乎完全命中。截取中间一段反而会让
        // token 序列与主对话错位，只剩 system 可复用。
        int keep = m_EphemeralHistoryMessages;
        int start = 0;
        if (keep > 0)
        {
            int seen = 0;
            start = m_DataList.Count;
            for (int i = m_DataList.Count - 1; i >= 0 && seen < keep; i--)
            {
                SendData item = m_DataList[i];
                if (item == null || item.role == "system") continue;
                seen++;
                start = i;
            }
        }
        for (int i = start; i < m_DataList.Count; i++)
        {
            SendData item = m_DataList[i];
            if (item == null || item.role == "system") continue;
            selected.Add(new SendData(item.role, item.content ?? ""));
        }
        // 顺序必须与主对话一致：[system][历史][记忆块]，草稿只在其后多一条指令。
        // 这样草稿 prompt 是主对话 prompt 的严格延长，两者共享同一段长前缀。
        if (!string.IsNullOrEmpty(TrailingContext))
            selected.Add(new SendData("system", TrailingContext));
        selected.Add(new SendData("user", prompt));

        var sb = new StringBuilder(2048);
        sb.Append('{');
        sb.Append("\"model\":");
        AppendJsonString(sb, CurrentModelName);
        // 非流式即可：实测 llama-server 在客户端断开时会取消任务，流式与否没有差别
        // （中断后紧接着的探测请求耗时与空闲基线一致，均为 0.25s）。
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
        AppendSlot(sb, k_SlotAuxiliary);   // BuildEphemeralRequestJson 投机草稿
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
        ResetThinkStrip();

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
                string clean = StripLeadingThinkBlock(delta);
                if (clean.Length == 0) return;
                if (_onDelta != null) _onDelta(clean);
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
                string full = StripLeadingThinkBlock(handler.GetFullContent(), true);
                m_DataList.Add(new SendData("assistant", MergeSpokenPrefix(full)));
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
    /// 历史裁剪采用高低水位，而不是每轮裁到固定条数。
    ///
    /// 旧实现把 targetBeforeRequest 定为 limit-2、每次请求都裁到这个数，而每轮正好新增
    /// user+assistant 两条，于是每轮都恰好删掉最老的两条——system 之后的 token 序列每轮
    /// 整体平移。对云端 Flash 模型这样做没错(prefill 按量计费、无缓存)，但本地 llama.cpp
    /// 有前缀缓存：稳定的长历史几乎免费，逐轮平移的短历史反而每轮全量重算。实测复用固定
    /// 停在 7368 token(= system prompt 长度)，其后 2600-4100 token 每轮重算，prompt 处理
    /// 5-10 秒。加载 --mmproj 后 KV 位移复用被禁用，所以没有部分复用的退路。
    ///
    /// 改法：只有超过高水位才裁，且一次裁到低水位，中间若干轮都是纯追加、可完整命中。
    /// </summary>
    public override void CheckHistory()
    {
        int limit = Mathf.Max(4, m_LowLatencyHistoryLimit);
        int nonSystemCount = 0;
        for (int i = 0; i < m_DataList.Count; i++)
        {
            if (m_DataList[i] != null && m_DataList[i].role != "system") nonSystemCount++;
        }
        //高水位：没超过就一条都不动，让这一轮成为纯追加
        if (nonSystemCount <= limit) return;
        //低水位。设每条消息 t 个 token，裁剪一次要重算 target*t，而涨回高水位需要
        //(limit-target)/2 轮，故平均每轮重算 2*t*target/(limit-target)。按固定比例取
        //target 时这个值与 limit 无关(0.6 倍 => 恒为 3t)，所以单纯抬高上限没有收益——
        //实测把上限 8 提到 12，中位数没怎么动，反而多出几次 11 秒的尖峰。要降的是比值：
        //低水位压低、高水位抬高。上下文扩到 32k 后 limit=32、0.25 倍 => 0.67t，
        //且裁剪间隔拉长到约 12 轮，双峰里那个慢峰因此变得罕见。
        int target = Mathf.Max(2, Mathf.RoundToInt(limit * 0.25f));

        int removed = 0;
        while (nonSystemCount > target)
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
            Debug.Log($"[LLM请求] 历史裁剪(高水位{limit}→低水位{target}): " +
                      $"移除{removed}条，保留{nonSystemCount}条旧消息");
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
