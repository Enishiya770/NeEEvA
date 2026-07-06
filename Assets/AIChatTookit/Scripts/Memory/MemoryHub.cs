using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆系统的对外门面——ChatSample 只需要持有这一个引用。
    /// 负责:
    ///   1. 启动时加载 / 初始化 MemoryStore,并做一次增量权重衰减
    ///   2. 拼出"记忆库"块(top-N 核心节点),供感知帧注入
    ///   3. 接收 LLM 的 <memory_add/> / <memory_update/> 写入操作(ApplyMemoryOps)
    ///
    /// 关键设计变更(2025): 不再做 query-driven 召回——LLM 自己有语义联想能力,
    /// 工程层只负责把核心节点放到她视野里, 不替她挑选哪些跟当前话题相关。
    /// </summary>
    public class MemoryHub : MonoBehaviour
    {
        [Header("种子记忆 (TextAsset, 仅在运行时文件不存在时使用)")]
        [SerializeField] private TextAsset m_SeedJson;

        [Header("运行时文件名 (在 Application.persistentDataPath 下)")]
        [SerializeField] private string m_RuntimeFileName = "memory.json";

        [Header("强制重置——勾上后启动时无视已有运行时文件,从种子重新初始化(开发期改种子用)")]
        [SerializeField] private bool m_ForceReseedOnStart = false;

        [Header("注入感知帧的 top-N 节点数")]
        [Tooltip("每帧把 top-N 核心节点放进感知帧。N 应控制在使 token 占用 < 30% ctx,典型值 20-40")]
        [Range(0, 200)]
        [SerializeField] private int m_MemoryMapTopN = 30;

        [Header("调试日志")]
        [Tooltip("勾上后,启动时会打印一次注入哪些节点(避免每帧刷屏)")]
        [SerializeField] private bool m_LogMemoryMap = false;

        [Header("LLM 写入 (<memory_add/> / <memory_update/>)")]
        [Tooltip("总开关。关掉后标签仍会被剥离(不会被念出来),只是不落库")]
        [SerializeField] private bool m_EnableLLMWrite = true;
        [Tooltip("节点总数上限,防失控膨胀。达到后新增被忽略,更新不受限")]
        [SerializeField] private int m_MaxNodes = 400;
        [Tooltip("新增节点未显式给 weight 时的默认权重")]
        [Range(0f, 1f)]
        [SerializeField] private float m_DefaultAddWeight = 0.6f;
        [Tooltip("打印每次写入操作")]
        [SerializeField] private bool m_LogMemoryWrites = true;

        [Header("权重衰减")]
        [Tooltip("启动时按距上次衰减的天数统一衰减一次。乘法衰减不改变相对排序,作用是让新记忆能压过久不提及的旧节点")]
        [SerializeField] private bool m_EnableDecay = true;
        [Tooltip("半衰期(天):这么多天不被提及,权重减半")]
        [SerializeField] private float m_DecayHalfLifeDays = 180f;
        [Tooltip("衰减下限,防止核心记忆彻底消失")]
        [Range(0f, 1f)]
        [SerializeField] private float m_DecayFloor = 0.15f;

        [Header("情境召回 (语义嵌入,可选) —— 既视感通道")]
        [Tooltip("按当前对话内容对全库做语义检索,把「此刻被唤起的记忆」注入感知帧。" +
                 "需要同物体上挂 EmbeddingClient 并部署嵌入服务;未配置时自动降级为提及扫描+图激活")]
        [SerializeField] private bool m_EnableSemanticRecall = true;
        [Tooltip("留空则自动在同一 GameObject 上查找 EmbeddingClient")]
        [SerializeField] private EmbeddingClient m_Embedding;
        [Tooltip("「此刻被唤起的记忆」最多注入几条")]
        [SerializeField] private int m_RecallTopK = 6;
        [Tooltip("进入唤起列表的最低得分(余弦相似度 + 激活加成)")]
        [Range(0f, 1f)]
        [SerializeField] private float m_RecallThreshold = 0.45f;
        [Tooltip("情境线索保鲜期(秒)——用户很久没说话后,旧线索不再驱动语义召回")]
        [SerializeField] private float m_QueryMaxAgeSec = 300f;
        [Tooltip("打印召回与激活细节")]
        [SerializeField] private bool m_LogRecall = false;

        [Header("扩散激活 (沿记忆网络的边联想)")]
        [Tooltip("被唤起/被提及/被写入的节点把能量沿边传给邻居——'A 让我想到 B'")]
        [SerializeField] private bool m_EnableActivation = true;
        [Tooltip("每跳能量衰减(顺边方向,边的 strength 也会乘进去)")]
        [Range(0f, 1f)]
        [SerializeField] private float m_HopDecay = 0.6f;
        [Tooltip("逆边折扣——'A 让我想到 B'不代表'B 让我想到 A'同样强")]
        [Range(0f, 1f)]
        [SerializeField] private float m_ReverseFactor = 0.5f;
        [Tooltip("激活能量的半衰期(秒)")]
        [SerializeField] private float m_ActivationHalfLifeSec = 180f;
        [Tooltip("激活值折算进召回得分的系数(有语境向量时)")]
        [Range(0f, 1f)]
        [SerializeField] private float m_ActivationWeight = 0.35f;
        [Tooltip("没有(新鲜)语境向量时,纯靠激活值进召回列表的门槛——直接提及(1.0)和一跳扩散(~0.55)能过,二跳(~0.2)过不了")]
        [Range(0f, 1.5f)]
        [SerializeField] private float m_ActivationOnlyThreshold = 0.5f;

        private MemoryStore m_Store;
        private bool m_FirstMapLogged = false;
        public MemoryStore Store { get { return m_Store; } }

        //---- 情境召回 / 扩散激活的运行时状态(不持久化,激活是"此刻在脑海里"的短时状态) ----
        private MemoryEmbeddingIndex m_VecIndex;
        private struct Act { public float v; public float t; }
        private readonly Dictionary<string, Act> m_Activation = new Dictionary<string, Act>();
        private float[] m_QueryVec;
        private float m_QueryTime = -1f;
        private bool m_EmbedInFlight;
        private float m_NextIndexCheck;

        void Awake()
        {
            m_Store = new MemoryStore();
            string seedJson = (m_SeedJson != null) ? m_SeedJson.text : null;
            m_Store.LoadOrSeed(m_RuntimeFileName, seedJson, m_ForceReseedOnStart);
            if (m_EnableDecay) m_Store.ApplyDecay(m_DecayHalfLifeDays, m_DecayFloor);

            if (m_Embedding == null) m_Embedding = GetComponent<EmbeddingClient>();
            m_VecIndex = new MemoryEmbeddingIndex();
            if (m_EnableSemanticRecall && m_Embedding != null)
            {
                m_VecIndex.Load("memory_embeddings.json", m_Embedding.ModelId);
                m_VecIndex.PruneMissing(m_Store.Nodes);
            }
            else if (m_EnableSemanticRecall)
            {
                Debug.Log("[Memory] 未找到 EmbeddingClient——语义召回停用,提及扫描与图激活仍然生效。" +
                          "在 MemoryHub 所在物体上添加 EmbeddingClient 组件即可启用。");
            }
        }

        /// <summary>
        /// 后台维护嵌入索引:分批把缺向量的节点送去嵌入(节点新增/描述被改后哈希失配会自动进入待办)。
        /// </summary>
        void Update()
        {
            if (!m_EnableSemanticRecall || m_Embedding == null || m_Store == null) return;
            if (m_EmbedInFlight || Time.realtimeSinceStartup < m_NextIndexCheck) return;
            m_NextIndexCheck = Time.realtimeSinceStartup + 3f;

            var pending = m_VecIndex.Pending(m_Store.Nodes);
            if (pending.Count == 0) return;

            int take = Mathf.Min(32, pending.Count);
            var batch = pending.GetRange(0, take);
            var texts = new List<string>(take);
            foreach (var n in batch) texts.Add(MemoryEmbeddingIndex.ContentOf(n));

            m_EmbedInFlight = true;
            m_Embedding.EmbedBatch(texts, vecs =>
            {
                m_EmbedInFlight = false;
                if (vecs == null)
                {
                    m_NextIndexCheck = Time.realtimeSinceStartup + 30f;   //服务不可用,放慢重试
                    return;
                }
                int ok = 0;
                for (int i = 0; i < batch.Count && i < vecs.Count; i++)
                    if (vecs[i] != null) { m_VecIndex.Set(batch[i], vecs[i]); ok++; }
                if (ok > 0) m_VecIndex.Save();
                if (m_LogRecall) Debug.Log($"[Memory] 嵌入索引 +{ok} 条 (待办剩 {pending.Count - take})");
            });
        }

        /// <summary>
        /// 应用 LLM 的记忆写入操作(来自 &lt;memory_add/&gt; / &lt;memory_update/&gt; 标签)。
        /// 两个方向都宽容:add 撞已有名字自动转更新,update 找不到节点自动转新增——
        /// LLM 不需要先确认节点是否存在。任何实际改动后立即原子落盘。
        /// </summary>
        public void ApplyMemoryOps(List<MemoryTagParser.MemoryOp> ops)
        {
            if (!m_EnableLLMWrite || ops == null || m_Store == null) return;

            bool dirty = false;
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                string name = Truncate(op.name, 48);
                if (string.IsNullOrEmpty(name)) continue;
                string desc = Truncate(op.desc, 160);

                var existing = m_Store.GetNode(name);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(desc)) existing.description = desc;
                    if (op.hasWeight) existing.weight = Mathf.Clamp01(op.weight);
                    existing.TouchActivated();
                    dirty = true;
                    if (m_LogMemoryWrites)
                        Debug.Log($"[Memory] 更新: {name} (weight={existing.weight:F2}) {existing.description}");
                }
                else
                {
                    if (m_Store.Nodes.Count >= m_MaxNodes)
                    {
                        Debug.LogWarning($"[Memory] 节点数已达上限 {m_MaxNodes},忽略新增: {name}");
                        continue;
                    }
                    float weight = op.hasWeight ? Mathf.Clamp01(op.weight) : m_DefaultAddWeight;
                    m_Store.AddNode(new MemoryNode(name, desc ?? "", weight));
                    dirty = true;
                    if (m_LogMemoryWrites)
                        Debug.Log($"[Memory] 新增: {name} (weight={weight:F2}) {desc}");
                }

                //她刚写下/强化的记忆是"此刻在脑海里"的——激活并向邻居扩散。
                //新增/更新过的节点内容哈希失配,Update() 的索引维护会自动重嵌。
                AddActivation(name, 1f);
                SpreadActivation(name, 0.8f);
            }

            if (dirty) m_Store.Save();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        //退出时落盘——LLM 写入或 TouchActivated 是内存改动,不主动保存就会丢。
        //OnApplicationQuit 在 Editor Play 模式停止和打包后退出都能正确触发。
        void OnApplicationQuit()
        {
            if (m_Store != null) m_Store.Save();
            if (m_VecIndex != null) m_VecIndex.Save();
        }

        // ==================== 情境召回 / 扩散激活 ====================

        /// <summary>用户开口时调用:名字提及立即激活(同步生效),并异步更新语境向量供后续帧的语义召回。</summary>
        public void NotifyUserUtterance(string text) { NotifyUtterance(text, true); }

        /// <summary>AI 发言(含内心独白)落定时调用:提及扫描与激活。语境向量以用户侧线索为准,这里不更新。</summary>
        public void NotifyAIUtterance(string text) { NotifyUtterance(text, false); }

        private void NotifyUtterance(string text, bool isUser)
        {
            if (m_Store == null || string.IsNullOrEmpty(text)) return;

            //名字提及扫描——节点名整串出现在发言里 = 明确想起:刷新激活时间并向邻居扩散
            foreach (var n in m_Store.Nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.name) || n.name.Length < 2) continue;
                if (text.IndexOf(n.name, System.StringComparison.Ordinal) < 0) continue;
                n.TouchActivated();
                AddActivation(n.name, 1f);
                SpreadActivation(n.name, 1f);
                if (m_LogRecall) Debug.Log($"[Memory] 提及激活: {n.name}");
            }

            //语境向量(既视感通道)。嵌入是异步 HTTP,本帧来不及用——结果作用于紧随其后的
            //续写/tick 帧,对话节奏上表现为"说着说着想起来",一拍以内。
            if (isUser && m_EnableSemanticRecall && m_Embedding != null)
            {
                string q = text.Length > 300 ? text.Substring(0, 300) : text;
                m_Embedding.Embed(q, vec =>
                {
                    if (vec == null) return;
                    m_QueryVec = vec;
                    m_QueryTime = Time.realtimeSinceStartup;
                    //命中的记忆立刻把邻居也带热——相关但不相似的节点也能浮出来
                    var hits = ComputeRecall(null);
                    foreach (var kv in hits) SpreadActivation(kv.Key.name, kv.Value);
                    if (m_LogRecall && hits.Count > 0)
                    {
                        var dbg = new StringBuilder("[Memory] 语境唤起:");
                        foreach (var kv in hits) dbg.Append(' ').Append(kv.Key.name).Append('(').Append(kv.Value.ToString("F2")).Append(')');
                        Debug.Log(dbg.ToString());
                    }
                });
            }
        }

        /// <summary>读取某节点当前激活值(按半衰期实时衰减,无需周期性遍历)。</summary>
        private float GetActivation(string name)
        {
            Act a;
            if (!m_Activation.TryGetValue(name, out a)) return 0f;
            float halfLife = Mathf.Max(1f, m_ActivationHalfLifeSec);
            return a.v * Mathf.Pow(0.5f, (Time.realtimeSinceStartup - a.t) / halfLife);
        }

        private void AddActivation(string name, float energy)
        {
            if (!m_EnableActivation || energy <= 0.01f) return;
            float cur = GetActivation(name);
            m_Activation[name] = new Act { v = Mathf.Min(1.5f, cur + energy), t = Time.realtimeSinceStartup };
        }

        /// <summary>
        /// 从源节点沿边扩散激活,最多两跳。顺边(from→to)全额,逆边按 m_ReverseFactor 打折——
        /// 尊重边的有向设计:"七日目"强烈唤起"重生",反向未必同样强。
        /// </summary>
        private void SpreadActivation(string source, float energy)
        {
            if (!m_EnableActivation || m_Store == null) return;
            var frontier = new List<KeyValuePair<string, float>> { new KeyValuePair<string, float>(source, energy) };
            var visited = new HashSet<string> { source };
            for (int hop = 0; hop < 2 && frontier.Count > 0; hop++)
            {
                var next = new List<KeyValuePair<string, float>>();
                foreach (var kv in frontier)
                {
                    foreach (var e in m_Store.Edges)
                    {
                        string other; float factor;
                        if (e.from == kv.Key) { other = e.to; factor = 1f; }
                        else if (e.to == kv.Key) { other = e.from; factor = m_ReverseFactor; }
                        else continue;
                        if (visited.Contains(other)) continue;
                        float en = kv.Value * e.strength * m_HopDecay * factor;
                        if (en < 0.05f) continue;
                        visited.Add(other);
                        AddActivation(other, en);
                        next.Add(new KeyValuePair<string, float>(other, en));
                        if (m_LogRecall) Debug.Log($"[Memory] 扩散: {kv.Key} → {other} ({en:F2})");
                    }
                }
                frontier = next;
            }
        }

        /// <summary>
        /// 计算「此刻被唤起的记忆」:得分 = 语境余弦相似度(线索新鲜时) + 激活加成。
        /// 全库暴力扫描——几百节点是微秒级。exclude 用来剔除已在核心列表里的节点。
        /// </summary>
        private List<KeyValuePair<MemoryNode, float>> ComputeRecall(HashSet<MemoryNode> exclude)
        {
            var result = new List<KeyValuePair<MemoryNode, float>>();
            if (m_Store == null) return result;
            bool queryFresh = m_QueryVec != null &&
                (Time.realtimeSinceStartup - m_QueryTime) <= m_QueryMaxAgeSec;

            foreach (var n in m_Store.Nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.name)) continue;
                if (exclude != null && exclude.Contains(n)) continue;

                float score; float threshold;
                if (queryFresh)
                {
                    //语义通道:相似度为主,激活做加成
                    score = 0f;
                    if (m_VecIndex != null)
                    {
                        float[] v;
                        if (m_VecIndex.TryGet(n, out v))
                            score += MemoryEmbeddingIndex.Cosine(m_QueryVec, v);
                    }
                    score += m_ActivationWeight * GetActivation(n.name);
                    threshold = m_RecallThreshold;
                }
                else
                {
                    //降级通道:没有(新鲜)语境向量时纯看激活——提及与一跳扩散能浮出来
                    score = GetActivation(n.name);
                    threshold = m_ActivationOnlyThreshold;
                }

                if (score >= threshold)
                    result.Add(new KeyValuePair<MemoryNode, float>(n, score));
            }
            result.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (result.Count > m_RecallTopK) result.RemoveRange(m_RecallTopK, result.Count - m_RecallTopK);
            return result;
        }

        /// <summary>
        /// 拼一个固定格式的记忆库块,供感知帧注入。
        /// 内容: 按重要性(weight × recency)排序的 top-N 节点,name + description。
        /// LLM 看到这一坨自己做语义关联——用户说"活着"她能自然想到"七日目を生還"等节点。
        /// </summary>
        public string BuildMemoryMap()
        {
            if (m_Store == null) return string.Empty;
            var picked = MemoryRanking.SelectTop(m_Store, m_MemoryMapTopN);
            if (picked.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n你的记忆 (按重要性,LLM 自己看哪些跟当前对话相关):");
            for (int i = 0; i < picked.Count; i++)
            {
                var nd = picked[i];
                sb.Append("\n  · ");
                sb.Append(nd.name);
                if (!string.IsNullOrEmpty(nd.description))
                {
                    sb.Append(" — ");
                    sb.Append(nd.description);
                }
            }

            //第二通道:此刻被情境唤起的记忆(语义相似 + 扩散激活),剔除已在核心列表里的。
            //每次建帧现算——激活值在实时衰减,语境向量也可能刚更新过。
            var coreSet = new HashSet<MemoryNode>(picked);
            var recalled = ComputeRecall(coreSet);
            if (recalled.Count > 0)
            {
                sb.Append("\n此刻被唤起的记忆 (由当前情境自动联想到,或许与正在发生的事有关):");
                foreach (var kv in recalled)
                {
                    var nd = kv.Key;
                    nd.TouchActivated();   //被唤起 = 被想起,刷新新近度
                    sb.Append("\n  · ").Append(nd.name);
                    if (!string.IsNullOrEmpty(nd.description))
                    {
                        sb.Append(" — ");
                        sb.Append(nd.description);
                    }
                }
            }

            //只在第一次打 log,避免每帧刷屏
            if (m_LogMemoryMap && !m_FirstMapLogged)
            {
                m_FirstMapLogged = true;
                var dbg = new StringBuilder();
                dbg.Append("[Memory] 注入感知帧 (top-").Append(picked.Count).Append("):");
                for (int i = 0; i < picked.Count; i++)
                {
                    if (i > 0) dbg.Append(", ");
                    dbg.Append(picked[i].name);
                }
                Debug.Log(dbg.ToString());
            }

            return sb.ToString();
        }
    }
}
