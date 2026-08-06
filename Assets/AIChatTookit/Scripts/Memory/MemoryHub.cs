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

        [Header("短期记忆 (气泡) — 这次对话发生的事，会消失")]
        [Tooltip("用户每说一句自动生成一个低热度气泡；她也可以用 <note/> 主动记高热度的。" +
                 "混合来源的理由：只靠她主动，很可能重蹈 <memory_link/> 的覆辙(加了半天零使用)；" +
                 "自动那半才是缺失的'当场提示'")]
        [SerializeField] private ShortTermMemory m_ShortTerm = new ShortTermMemory();

        [Header("统一记忆视图 (拓扑 + 热度 + 孤儿，一块交给 LLM)")]
        [Tooltip("身份层：weight 最高的这么多条永远显示。她是谁、你是谁、你们的关系——" +
                 "这不是'待整理的事实'，抽走会直接改变人格连续性，所以不参与任何筛选")]
        [Range(0, 30)] [SerializeField] private int m_ViewIdentityTopN = 8;
        [Tooltip("最多展开几个节点的连线。视图大小应该由'此刻在想什么'决定，" +
                 "而不是由库有多大决定——库涨到 400 节点时这一条是唯一的护栏")]
        [Range(0, 40)] [SerializeField] private int m_ViewMaxRoots = 12;
        [Tooltip("每个节点最多列几条出边")]
        [Range(0, 12)] [SerializeField] private int m_ViewMaxChildren = 6;
        [Tooltip("描述截断字数。实测库里描述中位 28 字、最长 51，截 48 只影响 3/26 条，基本无损。" +
                 "调到 20 可让整块比原来的三块式还省 5 token，代价是牺牲语义细节——" +
                 "记忆不该为延迟让路，所以默认取几乎无损的 48")]
        [Range(8, 200)] [SerializeField] private int m_ViewDescChars = 48;
        [Tooltip("孤立节点最多列几条。这是记忆整理的抓手：实测库里 5 个孤儿(她自己写的 4 个 + " +
                 "种子里 1 个)从来没被扩散激活够到过，因为扩散只沿边走")]
        [Range(0, 40)] [SerializeField] private int m_ViewMaxIsolated = 10;
        [Tooltip("是否显示边强度。库里实际有 0.80/0.85/0.90/0.95 四档，信息量弱但非零；" +
                 "她要决定是否加强某条边时需要看见当前值")]
        [SerializeField] private bool m_ViewShowEdgeStrength = true;

        //m_MemoryMapTopN 已由 m_ViewIdentityTopN 取代(统一视图的身份层)。
        //它原来的告诫仍然成立并适用于整个统一视图：这一块**不能**进感知帧，
        //否则会随整帧留在对话历史里被反复重放——实测占比过高时历史很快填满触发裁剪，
        //而裁剪会让 llama.cpp 前缀缓存失效、整段重算 5-10 秒(加载 --mmproj 后
        //KV 位移复用被禁用，没有部分复用的余地)。现在它走 TrailingContext。

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
        [Tooltip("<memory_link/> 未显式给 strength 时的默认边强度。边强度直接乘进扩散激活，" +
                 "0.6 配合每跳衰减 0.6 时，一跳能量约 0.36，两跳约 0.13")]
        [Range(0f, 1f)]
        [SerializeField] private float m_DefaultLinkStrength = 0.6f;
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

        [Header("待机漂移 (没人说话时让激活场自己动)")]
        [Tooltip("待机期间激活原本只会衰减到零,她醒来时'什么都没发生过'。开启后会低频地" +
                 "沿边点火,让她带着'刚才不由自主想到的东西'醒来。纯 C#,不调用推理服务")]
        [SerializeField] private bool m_EnableIdleDrift = true;
        [Tooltip("最后一次发言(用户或她自己)之后静默这么久才开始漂移——对话中不漂移")]
        [Range(10f, 300f)] [SerializeField] private float m_DriftIdleAfterSec = 45f;
        [Tooltip("两次漂移的最小间隔")]
        [Range(5f, 300f)] [SerializeField] private float m_DriftMinIntervalSec = 90f;
        [Tooltip("两次漂移的最大间隔。间隔必须显著长于激活半衰期(180s),否则能量层层叠加," +
                 "感知帧里会永远挂着一串'想到的事'。拿真实 memory.json 模拟 30 分钟,同时浮现的条数: " +
                 "25-75s/0.65 → 中位 4、最多 7、从不为空(刷屏); " +
                 "60-150s/0.60 → 中位 2、最多 3、6% 时间为空; " +
                 "90-210s/0.60 → 中位 1、最多 3、31% 时间为空(当前)。" +
                 "三成时间脑子里空着,比'永远有东西浮着'更像人")]
        [Range(10f, 600f)] [SerializeField] private float m_DriftMaxIntervalSec = 210f;
        [Tooltip("单次漂移注入的能量。要高于纯激活召回门槛(m_ActivationOnlyThreshold)才浮得到" +
                 "感知帧里,但只高一点点——这样它会在一两个间隔内自然沉下去,不赖着不走")]
        [Range(0f, 1.5f)] [SerializeField] private float m_DriftEnergy = 0.60f;
        [Tooltip("最近漂到过的这么多个节点不再重复点。只靠'邻居够凉了才点'挡不住来回横跳——" +
                 "温着的节点少时邻域就那么大,凉透了又重新合格,于是 A→B→A。" +
                 "同一组模拟里 0 → 3 让重复率 36% 降到 21%、触及节点 7.8 → 9.5,而浓度不变")]
        [Range(0, 8)] [SerializeField] private int m_DriftNoRepeatCount = 3;

        private MemoryStore m_Store;
        private bool m_FirstMapLogged = false;
        public MemoryStore Store { get { return m_Store; } }

        //---- 情境召回 / 扩散激活的运行时状态(不持久化,激活是"此刻在脑海里"的短时状态) ----
        private MemoryEmbeddingIndex m_VecIndex;
        private struct Act { public float v; public float t; }
        private readonly Dictionary<string, Act> m_Activation = new Dictionary<string, Act>();
        private float[] m_QueryVec;
        private float m_QueryTime = -1f;
        private float m_LastUtteranceTime = -99999f;
        private float m_NextDriftTime;
        private float m_PendingDriftEnergy;      //待冲动模型取走的漂移能量
        private readonly List<string> m_RecentDrift = new List<string>();
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
            IdleDrift();   //待机漂移不依赖嵌入服务，必须放在下面那道 return 之前

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
        /// <summary>
        /// 待机整理用的网络视图。只列真正需要她处理的东西：
        ///   1. 孤立节点——没有任何边，扩散激活到不了，等于游离在联想网络之外
        ///   2. 已有的边——让她知道什么已经连过了，避免重复
        /// 不把整张图倒进上下文：这块会随感知帧沉淀进历史，而它只在偶尔的整理帧出现。
        /// 没有孤立节点时返回空串——没什么可整理的就不打扰她。
        /// </summary>
        public string BuildConsolidationView(int maxIsolated = 12, int maxEdges = 40)
        {
            if (m_Store == null) return string.Empty;
            var linked = new HashSet<string>();
            foreach (var e in m_Store.Edges)
            {
                if (e == null) continue;
                if (!string.IsNullOrEmpty(e.from)) linked.Add(e.from);
                if (!string.IsNullOrEmpty(e.to)) linked.Add(e.to);
            }

            var isolated = new List<MemoryNode>();
            foreach (var n in m_Store.Nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.name)) continue;
                if (!linked.Contains(n.name)) isolated.Add(n);
            }
            if (isolated.Count == 0) return string.Empty;
            isolated.Sort((a, b) => b.weight.CompareTo(a.weight));

            var sb = new StringBuilder();
            sb.Append("\n[记忆整理] 现在没人说话，可以回头看看自己的记忆网络。");
            sb.Append("\n下面这些记忆还是孤立的——没有和任何其他记忆连起来，");
            sb.Append("所以聊到相关话题时它们不会自己浮上来:");
            int take = Mathf.Min(maxIsolated, isolated.Count);
            for (int i = 0; i < take; i++)
            {
                var nd = isolated[i];
                sb.Append("\n  · ").Append(nd.name);
                if (!string.IsNullOrEmpty(nd.description))
                    sb.Append(" — ").Append(nd.description);
            }
            if (isolated.Count > take)
                sb.Append($"\n  （还有 {isolated.Count - take} 条，这次先处理上面几条）");

            sb.Append("\n已经存在的联想（不要重复连）:");
            int shown = 0;
            foreach (var e in m_Store.Edges)
            {
                if (e == null) continue;
                if (shown >= maxEdges) { sb.Append("\n  …"); break; }
                sb.Append("\n  ").Append(e.from).Append(" → ").Append(e.to)
                  .Append(" (").Append(e.strength.ToString("F1")).Append(')');
                shown++;
            }
            sb.Append("\n用 <memory_link/> 把你觉得确实相关的连起来，一次连几条就好，不用连完。");
            sb.Append("这是你自己在心里整理，不要出声——配 <silent/> 使用。");
            return sb.ToString();
        }

        public void ApplyMemoryOps(List<MemoryTagParser.MemoryOp> ops)
        {
            if (!m_EnableLLMWrite || ops == null || m_Store == null) return;

            bool dirty = false;
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];

                if (op.isNote)
                {
                    //短期气泡不进长期图，因此不置 dirty、不落盘。
                    if (m_ShortTerm != null && !string.IsNullOrEmpty(op.desc))
                    {
                        m_ShortTerm.Add(op.desc, false, Time.realtimeSinceStartup, deliberate: true);
                        if (m_LogMemoryWrites) Debug.Log($"[Memory] <note/>: {op.desc}");
                    }
                    continue;
                }

                if (op.isLink)
                {
                    //写边。两端必须已存在——她不能凭空连到一个没写过的节点上，
                    //否则会造出只有边没有节点的悬挂引用。
                    string a = Truncate(op.from, 48);
                    string b = Truncate(op.to, 48);
                    if (m_Store.GetNode(a) == null || m_Store.GetNode(b) == null)
                    {
                        if (m_LogMemoryWrites)
                            Debug.LogWarning($"[Memory] 连边失败(节点不存在): {a} → {b}");
                        continue;
                    }
                    float st = op.hasStrength ? Mathf.Clamp(op.strength, 0f, 1f) : m_DefaultLinkStrength;
                    bool added = m_Store.SetEdge(a, b, st);
                    dirty = true;
                    if (m_LogMemoryWrites)
                        Debug.Log(st <= 0f
                            ? $"[Memory] 断开: {a} → {b}"
                            : $"[Memory] {(added ? "连边" : "改强度")}: {a} → {b} ({st:F2})");
                    //新建立的联想立刻生效：两端都点亮，让本轮之后的召回就能用上
                    AddActivation(a, 0.6f);
                    AddActivation(b, 0.6f);
                    continue;
                }

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
            //漂移只在真正的静默里发生，任何一方开口都重新计时
            m_LastUtteranceTime = Time.realtimeSinceStartup;

            //自动生成气泡。**只记用户侧**——她自己说的话已经在感知帧的「你最近发言」里，
            //再存一份是重复。这条路径在 ASR 幻听闸之后(被判为噪音的轮次拿不到文本，
            //根本进不来)，所以噪音不会变成气泡。
            if (isUser && m_ShortTerm != null)
                m_ShortTerm.Add(text, true, m_LastUtteranceTime);

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
        /// <summary>
        /// 待机漂移——没人说话时，让激活场自己动起来。
        ///
        /// 之前待机期间激活只会单调衰减到零，她醒来时"什么都没发生过"，
        /// 连续性全靠钟点数字撑着。这里在空闲时低频地点一下火：优先从**还温着的
        /// 节点沿边走一步**(联想漂移，"想着想着想到别处")，全凉了才按权重随机
        /// 唤起一条(自发回忆)。醒来时 `此刻被唤起的记忆` 里就有待机期间自己浮上
        /// 来的东西。
        ///
        /// 纯 C#，不调用任何推理服务——这是它相对"后台常驻推理"的全部意义。
        /// </summary>
        private void IdleDrift()
        {
            if (!m_EnableIdleDrift || m_Store == null || m_Store.Nodes.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            //对话中不漂移：那时该由真实语境驱动召回，随机联想只会添乱
            if (now - m_LastUtteranceTime < m_DriftIdleAfterSec) return;
            if (now < m_NextDriftTime) return;
            m_NextDriftTime = now + Random.Range(m_DriftMinIntervalSec, m_DriftMaxIntervalSec);

            //还温着的节点(高于扩散截断线即可，不必够到召回门槛)
            var warm = new List<MemoryNode>();
            foreach (var n in m_Store.Nodes)
                if (n != null && !string.IsNullOrEmpty(n.name) && GetActivation(n.name) > 0.12f)
                    warm.Add(n);

            MemoryNode target = null;
            string how;
            if (warm.Count > 0)
            {
                //联想漂移：从温着的节点沿边走一步
                var src = warm[Random.Range(0, warm.Count)];
                var neighbours = new List<string>();
                foreach (var e in m_Store.Edges)
                {
                    if (e == null) continue;
                    if (e.from == src.name) neighbours.Add(e.to);
                    else if (e.to == src.name) neighbours.Add(e.from);
                }
                //跳过还热着的、以及刚漂过的邻居，否则会在两个节点之间反复横跳
                for (int guard = 0; guard < 4 && neighbours.Count > 0; guard++)
                {
                    int i = Random.Range(0, neighbours.Count);
                    var cand = m_Store.GetNode(neighbours[i]);
                    neighbours.RemoveAt(i);
                    if (cand == null || GetActivation(cand.name) >= 0.3f) continue;
                    if (m_RecentDrift.Contains(cand.name)) continue;
                    target = cand;
                    break;
                }
                how = "联想";
            }
            else
            {
                how = "自发";
            }

            if (target == null)
            {
                //自发回忆：按权重加权随机，越核心的越容易自己浮上来。
                //先在"刚漂过的除外"里抽；全库都刚漂过(节点极少)时退回全库，不至于抽不出人。
                for (int pass = 0; pass < 2 && target == null; pass++)
                {
                    bool skipRecent = (pass == 0);
                    float total = 0f;
                    foreach (var n in m_Store.Nodes)
                    {
                        if (n == null || string.IsNullOrEmpty(n.name)) continue;
                        if (skipRecent && m_RecentDrift.Contains(n.name)) continue;
                        total += Mathf.Max(0.01f, n.weight);
                    }
                    if (total <= 0f) continue;
                    float pick = Random.Range(0f, total);
                    foreach (var n in m_Store.Nodes)
                    {
                        if (n == null || string.IsNullOrEmpty(n.name)) continue;
                        if (skipRecent && m_RecentDrift.Contains(n.name)) continue;
                        pick -= Mathf.Max(0.01f, n.weight);
                        if (pick <= 0f) { target = n; break; }
                    }
                }
            }
            if (target == null) return;

            m_RecentDrift.Add(target.name);
            while (m_RecentDrift.Count > m_DriftNoRepeatCount) m_RecentDrift.RemoveAt(0);

            AddActivation(target.name, m_DriftEnergy);
            SpreadActivation(target.name, m_DriftEnergy * 0.6f);
            m_PendingDriftEnergy += m_DriftEnergy;   //等冲动模型来取
            //漂移是低频事件(90-210s 一次)，独立于 m_LogRecall 打印——
            //上一版就是因为它没有日志，"漂移整场没跑过"这件事一直没被发现
            Debug.Log($"[Memory] 待机漂移({how}): {target.name} (+{m_DriftEnergy:F2})");
        }

        /// <summary>
        /// 取走自上次调用以来待机漂移注入的能量，并清零。返回 0 表示这期间没漂移过。
        ///
        /// 给冲动模型(UrgeModel)用：她忽然想起某件事，那件事就该推高她开口的冲动。
        /// 「想到了」和「想说」在真人身上本来就是一回事。
        ///
        /// 注意这里交出的是**事件**(一次漂移的能量)，不是激活场的水平量。曾经有个版本
        /// 用 TotalActivation() 之类的水平量当连续输入，实测是错的：每次发言(含她自己的)
        /// 都会做名字提及扫描并向外扩散，节点名她几乎每句都说，于是全库长期顶在上限附近，
        /// 那个量在对话期间恒定饱和、没有区分度，而且会随记忆库长大而变大。
        /// </summary>
        public float ConsumeDriftEnergy()
        {
            float v = m_PendingDriftEnergy;
            m_PendingDriftEnergy = 0f;
            return v;
        }

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
        /// 统一记忆视图：拓扑 + 热度 + 孤儿，一整块交给 LLM。
        ///
        /// 取代了原来的三块式(核心节点 / 此刻被唤起 / 刚才不由自主想到)。换掉的理由是
        /// 一个实测事实：**旧版一条边都不输出**，拓扑只在从未触发过的待机整理帧里出现，
        /// 所以她从来没见过这张图——却被要求用 &lt;memory_link/&gt; 把 A 和 B 连起来。
        /// 该标签上线至今零使用，库里新增边 0 条，她自己写的 4 个节点全是孤儿(0 条边)，
        /// 因而永远不会被扩散激活够到(扩散只沿边走)。
        ///
        /// 成本是中性的：把描述从"全文"截到 48 字换来整张拓扑，实测 767 → 约 838 token；
        /// 截到 20 字反而比旧版还省。整块必须留在 TrailingContext，别进感知帧——
        /// 它每轮都变，进历史会被反复重放。
        ///
        /// 选择策略让视图大小由"此刻在想什么"决定，而不是由库有多大决定：
        ///   身份层(固定几条) + 被唤起的节点 + 它们各自的一跳出边 + 全部孤立节点
        /// </summary>
        public string BuildMemoryMap()
        {
            if (m_Store == null) return string.Empty;

            var identity = MemoryRanking.SelectTop(m_Store, m_ViewIdentityTopN);
            var identitySet = new HashSet<MemoryNode>(identity);
            var recalled = ComputeRecall(identitySet);

            //被唤起 = 被想起,刷新新近度(保留旧版行为)
            var warm = new HashSet<MemoryNode>();
            foreach (var kv in recalled) { kv.Key.TouchActivated(); warm.Add(kv.Key); }

            //焦点 = 身份层 + 被唤起的。前者定义她是谁,后者定义她此刻在想什么。
            var focus = new List<MemoryNode>(identity);
            foreach (var kv in recalled) focus.Add(kv.Key);
            if (focus.Count == 0) return string.Empty;
            if (focus.Count > m_ViewMaxRoots) focus.RemoveRange(m_ViewMaxRoots, focus.Count - m_ViewMaxRoots);

            //区分来源：对话中浮上来的是"被这句话勾起的"，待机漂移来的是"自己想到的"。
            //她该怎么用这条记忆，取决于它是不是跟眼下的话题有关——这个区别放在图例里。
            bool fromContext = m_QueryVec != null &&
                (Time.realtimeSinceStartup - m_QueryTime) <= m_QueryMaxAgeSec;

            var sb = new StringBuilder();
            sb.Append("\n【你的记忆】○ 是安静着的");
            if (warm.Count > 0)
            {
                sb.Append("；● ").Append(fromContext
                    ? "是此刻被眼前的事勾起来的"
                    : "是安静时自己浮上来的(和眼下的话题未必有关)");
            }
            sb.Append("。缩进的箭头是你自己建立的联想。");

            //已渲染的节点集合——被召回的孤儿会同时命中焦点区和"漂浮着"区，
            //不去重的话整条描述会重复一遍(实测多花约 110 token)。
            var rendered = new HashSet<string>();
            foreach (var nd in focus)
            {
                sb.Append('\n').Append(warm.Contains(nd) ? "● " : "○ ").Append(nd.name);
                if (!string.IsNullOrEmpty(nd.description))
                    sb.Append(" — ").Append(Truncate(nd.description, m_ViewDescChars));
                rendered.Add(nd.name);

                var outs = m_Store.GetOutgoingEdges(nd.name);
                if (outs.Count == 0 && m_Store.GetIncomingEdges(nd.name).Count == 0)
                {
                    //焦点区里的孤儿：没有箭头这件事本身看不出是"孤立"还是"边被截断了"，
                    //得明说，否则她不知道这条正等着被连上。
                    sb.Append("   ← 还没连上任何东西");
                    continue;
                }
                int shown = Mathf.Min(outs.Count, m_ViewMaxChildren);
                for (int i = 0; i < shown; i++)
                {
                    sb.Append("\n   ").Append(i == shown - 1 ? "└→ " : "├→ ").Append(outs[i].to);
                    if (m_ViewShowEdgeStrength)
                        sb.Append(" (").Append(outs[i].strength.ToString("F2")).Append(')');
                }
                if (outs.Count > shown) sb.Append("\n   …还有 ").Append(outs.Count - shown).Append(" 条");
            }

            AppendIsolated(sb, rendered);

            //气泡接在长期图之后：先看见"我知道什么"，再看见"刚才发生了什么"，
            //两者挨着才谈得上把后者收进前者。
            if (m_ShortTerm != null)
                sb.Append(m_ShortTerm.Render(Time.realtimeSinceStartup));

            if (m_LogMemoryMap && !m_FirstMapLogged)
            {
                m_FirstMapLogged = true;
                var dbg = new StringBuilder();
                dbg.Append("[Memory] 统一视图: 焦点 ").Append(focus.Count)
                   .Append(" 条(其中温着 ").Append(warm.Count).Append("):");
                for (int i = 0; i < focus.Count; i++)
                {
                    if (i > 0) dbg.Append(", ");
                    dbg.Append(focus[i].name);
                }
                Debug.Log(dbg.ToString());
            }

            return sb.ToString();
        }

        /// <summary>
        /// 孤立节点单独成段。它们进不了拓扑那部分——没有边就没有缩进箭头可画，
        /// 混在里面反而看不出"这条还没挂上去"。这一段是记忆整理唯一的抓手。
        /// </summary>
        private void AppendIsolated(StringBuilder sb, HashSet<string> alreadyRendered)
        {
            if (m_ViewMaxIsolated <= 0) return;
            var isolated = new List<MemoryNode>();
            foreach (var n in m_Store.Nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.name)) continue;
                if (alreadyRendered != null && alreadyRendered.Contains(n.name)) continue;
                if (m_Store.GetOutgoingEdges(n.name).Count > 0) continue;
                if (m_Store.GetIncomingEdges(n.name).Count > 0) continue;
                isolated.Add(n);
                if (isolated.Count >= m_ViewMaxIsolated) break;
            }
            if (isolated.Count == 0) return;

            sb.Append("\n\n漂浮着 (还没连上任何东西——它们不会被联想到，" +
                      "如果哪条跟上面某个有关，用 <memory_link/> 连起来):");
            foreach (var n in isolated)
            {
                sb.Append("\n  · ").Append(n.name);
                if (!string.IsNullOrEmpty(n.description))
                    sb.Append(" — ").Append(Truncate(n.description, m_ViewDescChars));
            }
        }
    }
}
