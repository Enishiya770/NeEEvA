using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆网络的内存表达 + 持久化层。
    ///
    /// 文件布局:
    ///   - 种子(只读模板)由 Inspector 指定 TextAsset,跟着 build 走
    ///   - 运行时数据写在 Application.persistentDataPath/memory.json
    ///   - 第一次启动:运行时文件不存在 → 拷贝种子内容到运行时位置
    ///   - 之后所有读写都对运行时文件
    ///
    /// 这一层只做 IO 与基本 CRUD,不做评分/召回(那是 MemoryRecall 的活)。
    /// </summary>
    public class MemoryStore
    {
        [Serializable]
        private class FileFormat
        {
            public int version = 1;
            //ISO 8601,上次全局衰减时间。老文件缺此字段 → 从当下开始计,不追溯扣分
            public string last_decayed;
            public List<MemoryNode> nodes = new List<MemoryNode>();
            public List<MemoryEdge> edges = new List<MemoryEdge>();
        }

        public List<MemoryNode> Nodes { get { return m_File.nodes; } }
        public List<MemoryEdge> Edges { get { return m_File.edges; } }
        public string RuntimePath { get { return m_RuntimePath; } }

        private FileFormat m_File = new FileFormat();
        private string m_RuntimePath;
        private Dictionary<string, MemoryNode> m_NodeIndex = new Dictionary<string, MemoryNode>();

        /// <summary>
        /// 加载或初始化。seedJson 为种子 JSON 文本(通常来自 TextAsset.text),
        /// 仅在运行时文件不存在时使用。
        /// forceReseed=true 时无视已有运行时文件,从种子重新初始化(开发期改种子格式后用)。
        /// </summary>
        public void LoadOrSeed(string runtimeFileName, string seedJson, bool forceReseed = false)
        {
            m_RuntimePath = Path.Combine(Application.persistentDataPath, runtimeFileName);

            string json;
            if (!forceReseed && File.Exists(m_RuntimePath))
            {
                json = File.ReadAllText(m_RuntimePath);
                Debug.Log($"[MemoryStore] 加载运行时记忆: {m_RuntimePath}");
            }
            else
            {
                if (string.IsNullOrEmpty(seedJson))
                {
                    Debug.LogWarning("[MemoryStore] 没有种子 JSON,以空网络启动");
                    json = "{\"version\":1,\"nodes\":[],\"edges\":[]}";
                }
                else
                {
                    json = seedJson;
                    Debug.Log(forceReseed
                        ? $"[MemoryStore] 强制重置,从种子覆盖 → {m_RuntimePath}"
                        : $"[MemoryStore] 首次运行,从种子初始化 → {m_RuntimePath}");
                }
                //写出运行时文件,后续都用它
                try { File.WriteAllText(m_RuntimePath, json); }
                catch (Exception e) { Debug.LogError($"[MemoryStore] 写运行时文件失败: {e.Message}"); }
            }

            try
            {
                m_File = JsonUtility.FromJson<FileFormat>(json) ?? new FileFormat();
                if (m_File.nodes == null) m_File.nodes = new List<MemoryNode>();
                if (m_File.edges == null) m_File.edges = new List<MemoryEdge>();
            }
            catch (Exception e)
            {
                Debug.LogError($"[MemoryStore] 解析失败: {e.Message},以空网络启动");
                m_File = new FileFormat();
            }

            RebuildIndex();
            Debug.Log($"[MemoryStore] 已加载 {m_File.nodes.Count} 节点 / {m_File.edges.Count} 边");
        }

        /// <summary>
        /// 把当前内存状态原子写回磁盘(写临时文件后 replace,避免半截文件)。
        /// 对 < 1000 节点的网络几乎没成本,可以放心每次写入后调用。
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(m_RuntimePath))
            {
                Debug.LogWarning("[MemoryStore] 未初始化,无法保存");
                return;
            }
            try
            {
                string json = JsonUtility.ToJson(m_File, prettyPrint: true);
                string tmp = m_RuntimePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(m_RuntimePath)) File.Replace(tmp, m_RuntimePath, null);
                else File.Move(tmp, m_RuntimePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MemoryStore] 保存失败: {e.Message}");
            }
        }

        /// <summary>
        /// 全局权重衰减:按距上次衰减经过的天数,对所有节点乘同一半衰期因子,衰到 floor 为止。
        /// 增量式——每段真实流逝的时间只被计入一次,频繁重启不会重复扣。
        /// 相对排序不变(乘法),衰减的意义在于让 LLM 新写入的记忆能压过久不提及的旧节点。
        /// </summary>
        public void ApplyDecay(float halfLifeDays, float floor)
        {
            if (m_File == null || halfLifeDays <= 0f) return;

            DateTime now = DateTime.UtcNow;
            DateTime last;
            bool hasLast = DateTime.TryParse(m_File.last_decayed, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out last);

            if (!hasLast)
            {
                //老文件/首次:只打时间戳,不对历史追溯衰减
                m_File.last_decayed = now.ToString("o");
                Save();
                return;
            }

            double days = (now - last.ToUniversalTime()).TotalDays;
            if (days < 0.25) return;   //不到6小时不折腾,也不刷新时间戳

            float factor = (float)Math.Pow(0.5, days / halfLifeDays);
            int touched = 0;
            for (int i = 0; i < m_File.nodes.Count; i++)
            {
                var n = m_File.nodes[i];
                if (n == null) continue;
                float w = Mathf.Max(floor, n.weight * factor);
                if (Mathf.Abs(w - n.weight) > 0.0001f) { n.weight = w; touched++; }
            }
            m_File.last_decayed = now.ToString("o");
            Save();
            if (touched > 0)
                Debug.Log($"[MemoryStore] 衰减: 距上次 {days:F1} 天,因子 {factor:F4},{touched} 个节点受影响");
        }

        public MemoryNode GetNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            MemoryNode n;
            return m_NodeIndex.TryGetValue(name, out n) ? n : null;
        }

        /// <summary>
        /// 添加新节点。若同名已存在,返回 false(不覆盖,由调用方决定走 Update 路径)。
        /// </summary>
        public bool AddNode(MemoryNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.name)) return false;
            if (m_NodeIndex.ContainsKey(node.name)) return false;
            m_File.nodes.Add(node);
            m_NodeIndex[node.name] = node;
            return true;
        }

        public List<MemoryEdge> GetOutgoingEdges(string nodeName)
        {
            var result = new List<MemoryEdge>();
            for (int i = 0; i < m_File.edges.Count; i++)
                if (m_File.edges[i].from == nodeName) result.Add(m_File.edges[i]);
            return result;
        }

        public List<MemoryEdge> GetIncomingEdges(string nodeName)
        {
            var result = new List<MemoryEdge>();
            for (int i = 0; i < m_File.edges.Count; i++)
                if (m_File.edges[i].to == nodeName) result.Add(m_File.edges[i]);
            return result;
        }

        private void RebuildIndex()
        {
            m_NodeIndex.Clear();
            for (int i = 0; i < m_File.nodes.Count; i++)
            {
                var n = m_File.nodes[i];
                if (n != null && !string.IsNullOrEmpty(n.name))
                    m_NodeIndex[n.name] = n;
            }
        }
    }
}
