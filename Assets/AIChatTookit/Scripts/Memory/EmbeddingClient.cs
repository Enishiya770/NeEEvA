using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace AIChat.Memory
{
    /// <summary>
    /// OpenAI 兼容的 /v1/embeddings 客户端——给记忆系统的语义召回用。
    ///
    /// 两种典型后端:
    ///   本地: llama-server 加载嵌入模型(如 bge-m3 GGUF)并带 --embeddings 启动
    ///         llama-server -m bge-m3-Q8_0.gguf --embeddings --port 8090
    ///   云端: DashScope 兼容模式 https://dashscope.aliyuncs.com/compatible-mode/v1/embeddings
    ///         (model 填 text-embedding-v4 等,api_key 必填)
    ///
    /// 挂在 MemoryHub 同一个 GameObject 上即可被自动发现。
    /// 服务不可用时只警告一次,调用方拿到 null 自行降级。
    /// </summary>
    public class EmbeddingClient : MonoBehaviour
    {
        [Header("嵌入服务地址 (OpenAI 兼容 /v1/embeddings)")]
        [SerializeField] private string m_Url = "http://127.0.0.1:8090/v1/embeddings";
        [Header("模型名 (本地 llama-server 可随意填)")]
        [SerializeField] private string m_Model = "bge-m3";
        [Header("API Key (本地服务留空)")]
        [SerializeField] private string m_ApiKey = "";
        [Header("单次请求超时(秒)")]
        [SerializeField] private int m_TimeoutSec = 10;
        [Tooltip("本地嵌入服务不可用时暂停重试，避免每个Agent轮次都额外等待连接失败。")]
        [SerializeField] private float m_FailureCooldownSec = 30f;
        [SerializeField] private bool m_LogRequests = false;

        private bool m_WarnedOnce;
        private float m_RetryAfterRealtime = -999f;

        /// <summary>模型标识——换模型或换服务地址时嵌入缓存整体失效。</summary>
        public string ModelId { get { return m_Model + "@" + m_Url; } }

        /// <summary>单条文本 → 向量。失败回调 null。</summary>
        public void Embed(string text, Action<float[]> onDone)
        {
            EmbedBatch(new List<string> { text }, vecs =>
                onDone(vecs != null && vecs.Count > 0 ? vecs[0] : null));
        }

        /// <summary>一批文本 → 向量列表(与输入等长同序)。失败回调 null。</summary>
        public void EmbedBatch(List<string> texts, Action<List<float[]>> onDone)
        {
            if (texts == null || texts.Count == 0) { onDone(new List<float[]>()); return; }
            if (!isActiveAndEnabled) { onDone(null); return; }
            if (Time.realtimeSinceStartup < m_RetryAfterRealtime) { onDone(null); return; }
            StartCoroutine(Request(texts, onDone));
        }

        private IEnumerator Request(List<string> texts, Action<List<float[]>> onDone)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"model\":");
            AppendJsonString(sb, m_Model);
            sb.Append(",\"input\":[");
            for (int i = 0; i < texts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, texts[i] ?? "");
            }
            sb.Append("]}");

            using (var req = new UnityWebRequest(m_Url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(m_ApiKey))
                    req.SetRequestHeader("Authorization", "Bearer " + m_ApiKey);
                req.timeout = m_TimeoutSec;

                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();

                if (req.responseCode != 200)
                {
                    m_RetryAfterRealtime = Time.realtimeSinceStartup + Mathf.Max(1f, m_FailureCooldownSec);
                    if (!m_WarnedOnce)
                    {
                        m_WarnedOnce = true;
                        Debug.LogWarning($"[Embedding] 请求失败(code={req.responseCode}): {req.error} —— " +
                            "语义召回将降级为提及扫描+图激活。检查嵌入服务是否已启动: " + m_Url);
                    }
                    onDone(null);
                    yield break;
                }

                m_RetryAfterRealtime = -999f;
                var vecs = ParseVectors(req.downloadHandler.text, texts.Count);
                if (m_LogRequests)
                    Debug.Log($"[Embedding] {texts.Count} 条, 耗时 {(Time.realtimeSinceStartup - t0) * 1000f:F0}ms" +
                        (vecs != null && vecs.Count > 0 && vecs[0] != null ? $", dim={vecs[0].Length}" : ", 解析失败"));
                onDone(vecs);
            }
        }

        /// <summary>
        /// 解析 OpenAI 格式响应 {"data":[{"embedding":[...],"index":0},...]}。
        /// 手写扫描而不用 JsonUtility——嵌套数组的 float 精度与字段可选性用手写更稳。
        ///
        /// index 字段归位:JSON 对象内键序无保证,"index" 可能写在 "embedding" 之前或之后,
        /// 但同一个响应出自同一个序列化器,键序是一致的——先探测一次格式:
        ///   index 在前 → 本条 index = 数组开始位置向前最近的 "index"(不越过上一条数组结尾)
        ///   index 在后 → 本条 index = 数组结束位置向后最近的 "index"(不越过下一条 embedding)
        /// 响应里没有 index 键时按到达顺序编号(OpenAI 规范要求 data 与 input 同序)。
        /// </summary>
        private static List<float[]> ParseVectors(string json, int expected)
        {
            try
            {
                var result = new List<float[]>(new float[expected][]);
                int firstIdx = json.IndexOf("\"index\"", StringComparison.Ordinal);
                int firstEmb = json.IndexOf("\"embedding\"", StringComparison.Ordinal);
                bool indexFirst = firstIdx >= 0 && firstIdx < firstEmb;

                int cursor = 0, found = 0, prevRb = 0;
                while (true)
                {
                    int embKey = json.IndexOf("\"embedding\"", cursor, StringComparison.Ordinal);
                    if (embKey < 0) break;
                    int lb = json.IndexOf('[', embKey);
                    if (lb < 0) break;
                    int rb = json.IndexOf(']', lb);
                    if (rb < 0) break;

                    string body = json.Substring(lb + 1, rb - lb - 1);
                    string[] parts = body.Split(',');
                    var vec = new float[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                        vec[i] = float.Parse(parts[i],
                            System.Globalization.CultureInfo.InvariantCulture);

                    int idx = found;   //兜底:按到达顺序
                    if (firstIdx >= 0)
                    {
                        int parsed;
                        if (indexFirst)
                        {
                            int p = json.LastIndexOf("\"index\"", lb, StringComparison.Ordinal);
                            if (p >= prevRb && TryParseIndexAt(json, p, out parsed)) idx = parsed;
                        }
                        else
                        {
                            int nextEmb = json.IndexOf("\"embedding\"", rb, StringComparison.Ordinal);
                            int p = json.IndexOf("\"index\"", rb, StringComparison.Ordinal);
                            if (p >= 0 && (nextEmb < 0 || p < nextEmb) && TryParseIndexAt(json, p, out parsed)) idx = parsed;
                        }
                    }

                    if (idx >= 0 && idx < expected) result[idx] = vec;
                    found++;
                    prevRb = rb;
                    cursor = rb;
                }
                return found > 0 ? result : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Embedding] 响应解析失败: " + e.Message);
                return null;
            }
        }

        /// <summary>从 "index" 键的位置解析冒号后的整数值。</summary>
        private static bool TryParseIndexAt(string json, int keyPos, out int idx)
        {
            idx = -1;
            int colon = json.IndexOf(':', keyPos);
            if (colon < 0) return false;
            int p = colon + 1;
            while (p < json.Length && json[p] == ' ') p++;
            int start = p;
            while (p < json.Length && char.IsDigit(json[p])) p++;
            if (p == start) return false;
            return int.TryParse(json.Substring(start, p - start), out idx);
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
