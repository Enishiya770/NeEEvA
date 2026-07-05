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

        private MemoryStore m_Store;
        private bool m_FirstMapLogged = false;
        public MemoryStore Store { get { return m_Store; } }

        void Awake()
        {
            m_Store = new MemoryStore();
            string seedJson = (m_SeedJson != null) ? m_SeedJson.text : null;
            m_Store.LoadOrSeed(m_RuntimeFileName, seedJson, m_ForceReseedOnStart);
            if (m_EnableDecay) m_Store.ApplyDecay(m_DecayHalfLifeDays, m_DecayFloor);
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
