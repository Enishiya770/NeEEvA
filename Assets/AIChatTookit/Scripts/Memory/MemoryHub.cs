using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆系统的对外门面——ChatSample 只需要持有这一个引用。
    /// 负责:
    ///   1. 启动时加载 / 初始化 MemoryStore
    ///   2. 拼出"记忆库"块(top-N 核心节点),供感知帧注入
    ///   3. (后续步骤) 接收 LLM 的 <memory_*/> 写入操作
    ///   4. (后续步骤) 周期性衰减
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

        private MemoryStore m_Store;
        private bool m_FirstMapLogged = false;
        public MemoryStore Store { get { return m_Store; } }

        void Awake()
        {
            m_Store = new MemoryStore();
            string seedJson = (m_SeedJson != null) ? m_SeedJson.text : null;
            m_Store.LoadOrSeed(m_RuntimeFileName, seedJson, m_ForceReseedOnStart);
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
