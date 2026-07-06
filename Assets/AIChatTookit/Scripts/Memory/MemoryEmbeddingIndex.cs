using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆节点的向量索引 + 磁盘缓存。
    ///
    /// 缓存按「name → (内容哈希, 向量)」存,内容哈希 = SHA1(name + \n + description)——
    /// 节点描述被 LLM 更新后哈希失配,自动视为待重嵌。缓存文件与 memory.json 同目录,
    /// 删掉它只会触发一次全量重嵌,无数据风险。
    ///
    /// 规模假设:节点上限 400、维度 ~1024,暴力余弦是微秒级,不需要向量库。
    /// </summary>
    public class MemoryEmbeddingIndex
    {
        [Serializable]
        private class Entry
        {
            public string name;
            public string hash;
            public float[] vec;
        }

        [Serializable]
        private class CacheFile
        {
            public int version = 1;
            public string model;      //换嵌入模型时整体失效
            public List<Entry> entries = new List<Entry>();
        }

        private CacheFile m_Cache = new CacheFile();
        private readonly Dictionary<string, Entry> m_Index = new Dictionary<string, Entry>();
        private string m_Path;

        public void Load(string fileName, string modelId)
        {
            m_Path = Path.Combine(Application.persistentDataPath, fileName);
            m_Cache = new CacheFile { model = modelId };
            m_Index.Clear();
            try
            {
                if (File.Exists(m_Path))
                {
                    var loaded = JsonUtility.FromJson<CacheFile>(File.ReadAllText(m_Path));
                    if (loaded != null && loaded.entries != null && loaded.model == modelId)
                    {
                        m_Cache = loaded;
                        foreach (var e in m_Cache.entries)
                            if (e != null && !string.IsNullOrEmpty(e.name)) m_Index[e.name] = e;
                    }
                    else if (loaded != null && loaded.model != modelId)
                    {
                        Debug.Log($"[EmbeddingIndex] 嵌入模型从 {loaded.model} 换为 {modelId},缓存整体失效");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EmbeddingIndex] 缓存加载失败,将重建: " + e.Message);
            }
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(m_Path)) return;
            try
            {
                string json = JsonUtility.ToJson(m_Cache);
                string tmp = m_Path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(m_Path)) File.Replace(tmp, m_Path, null);
                else File.Move(tmp, m_Path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EmbeddingIndex] 缓存保存失败: " + e.Message);
            }
        }

        public static string ContentOf(MemoryNode n) => n.name + "\n" + (n.description ?? "");

        private static string HashOf(MemoryNode n)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(ContentOf(n)));
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>取节点向量;内容已变(哈希失配)视为没有。</summary>
        public bool TryGet(MemoryNode n, out float[] vec)
        {
            vec = null;
            Entry e;
            if (!m_Index.TryGetValue(n.name, out e) || e.vec == null || e.vec.Length == 0) return false;
            if (e.hash != HashOf(n)) return false;
            vec = e.vec;
            return true;
        }

        public void Set(MemoryNode n, float[] vec)
        {
            Entry e;
            if (!m_Index.TryGetValue(n.name, out e))
            {
                e = new Entry { name = n.name };
                m_Index[n.name] = e;
                m_Cache.entries.Add(e);
            }
            e.hash = HashOf(n);
            e.vec = vec;
        }

        /// <summary>列出还没有(有效)向量的节点。</summary>
        public List<MemoryNode> Pending(List<MemoryNode> nodes)
        {
            var result = new List<MemoryNode>();
            foreach (var n in nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.name)) continue;
                float[] _;
                if (!TryGet(n, out _)) result.Add(n);
            }
            return result;
        }

        /// <summary>清掉已不存在的节点的缓存(节点被删除/改名后)。</summary>
        public void PruneMissing(List<MemoryNode> nodes)
        {
            var alive = new HashSet<string>();
            foreach (var n in nodes) if (n != null) alive.Add(n.name);
            m_Cache.entries.RemoveAll(e => e == null || !alive.Contains(e.name));
            var dead = new List<string>();
            foreach (var k in m_Index.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (var k in dead) m_Index.Remove(k);
        }

        /// <summary>余弦相似度。维度不符或零向量返回 0。</summary>
        public static float Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0f;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += (double)a[i] * b[i];
                na += (double)a[i] * a[i];
                nb += (double)b[i] * b[i];
            }
            if (na <= 0 || nb <= 0) return 0f;
            return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
        }
    }
}
