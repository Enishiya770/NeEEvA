using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AIChat.Memory
{
    /// <summary>
    /// 短期记忆——「气泡」。这次对话里发生的事，会随时间缩小直至消失，
    /// 值得长期留下的由她自己用 &lt;memory_add/&gt; 收进长期图。
    ///
    /// 为什么需要它(实测依据)：
    ///   1. 这次对话说的新事情，在被写成长期节点之前**只存在于聊天历史里**，
    ///      而 ChatQW 在 32 条消息时裁剪历史——裁掉就彻底没了。
    ///   2. 更关键的是时机问题。实测被她实际用上的标签都有一个共同点：
    ///      感知帧里有当场的、可操作的提示——`视觉: 闭眼(用 <look/> 可以睁眼)` → &lt;look/&gt; 被用了；
    ///      `[演唱片段; 歌唱概率:0.63]` → &lt;song_search/&gt; 被触发。
    ///      而记忆标签**没有任何逐帧提示**，只有系统提示词里的静态规则，
    ///      结果是一整场对话里用户讲了爱好、兴趣、阅读习惯，她一条都没记。
    ///      气泡段落就是那个缺失的当场提示：把"刚才发生了什么、哪些还没进长期图"
    ///      摆在她眼前，末尾直接说"值得留下的收进上面的图"。
    ///
    /// 与长期层的分工：气泡**会消失**，长期节点只慢衰减且有下限(0.15)，永不归零。
    /// 「随时间缩小直至消失」只作用于这一层。
    ///
    /// 不持久化：半衰期只有二十几分钟，重启后本来就该忘光，存盘没有意义。
    /// </summary>
    [Serializable]
    public class ShortTermMemory
    {
        public class Bubble
        {
            public string text;
            public bool fromUser;
            public float heat;        //上次刷新时的热度
            public float lastTouch;   //realtimeSinceStartup
            public int touches;       //被重复提起过几次
        }

        [Tooltip("总开关。关掉后气泡段落不再出现，长期图部分不受影响")]
        [SerializeField] private bool m_Enabled = true;

        [Tooltip("气泡总数上限。超出时丢弃最凉的那个")]
        [Range(4, 64)] [SerializeField] private int m_Capacity = 20;

        [Tooltip("热度半衰期(秒)。**不要照抄激活场的 180 秒**——那是'此刻在脑海里'的尺度，" +
                 "而短期记忆要能撑过一整段对话。实测她平均每分钟说一次话，" +
                 "1500 秒(25 分钟)意味着半小时前说的事还剩不到一半热度，符合直觉")]
        [Range(120f, 7200f)] [SerializeField] private float m_HalfLifeSec = 1500f;

        [Tooltip("热度低于此值就彻底删除——这就是'直至消失'")]
        [Range(0.01f, 0.5f)] [SerializeField] private float m_ForgetBelow = 0.12f;

        [Tooltip("用户说一句话自动生成的气泡热度。工程层不替她判断哪句重要，" +
                 "只是保证'她说过'这件事不会因为历史被裁剪而彻底消失")]
        [Range(0f, 1.5f)] [SerializeField] private float m_UserHeat = 0.55f;

        [Tooltip("她主动用 <note/> 记下的气泡热度。比自动生成的高——那是她自己觉得要紧的")]
        [Range(0f, 1.5f)] [SerializeField] private float m_NoteHeat = 0.95f;

        [Tooltip("同一件事被再次提起时的热度加成(这就是草图里的'若被联想到则加强，放大')")]
        [Range(0f, 1f)] [SerializeField] private float m_RetouchBoost = 0.3f;

        [Tooltip("最多显示几条(按热度)。这是 token 预算的硬闸")]
        [Range(0, 20)] [SerializeField] private int m_MaxShown = 8;

        [Tooltip("单条气泡的文本截断字数")]
        [Range(10, 120)] [SerializeField] private int m_TextChars = 46;

        [Tooltip("打印气泡的生成与消失")]
        [SerializeField] private bool m_LogBubbles = false;

        private readonly List<Bubble> m_Bubbles = new List<Bubble>();

        public bool Enabled { get { return m_Enabled; } }
        public int Count { get { return m_Bubbles.Count; } }

        public void Clear() { m_Bubbles.Clear(); }

        public float HeatOf(Bubble b, float now)
        {
            if (b == null) return 0f;
            return b.heat * Mathf.Pow(0.5f, (now - b.lastTouch) / Mathf.Max(1f, m_HalfLifeSec));
        }

        /// <summary>
        /// 记一条气泡。文本与已有气泡高度重合时不新增，而是给那条加热——
        /// 同一件事被反复提起本来就该更难忘，这也避免了复读把气泡池冲垮。
        /// </summary>
        public void Add(string text, bool fromUser, float now, bool deliberate = false)
        {
            if (!m_Enabled) return;
            text = Normalize(text);
            if (string.IsNullOrEmpty(text)) return;

            float heat = deliberate ? m_NoteHeat : m_UserHeat;

            var dup = FindSimilar(text);
            if (dup != null)
            {
                dup.heat = Mathf.Min(1.5f, HeatOf(dup, now) + m_RetouchBoost);
                dup.lastTouch = now;
                dup.touches++;
                if (text.Length > dup.text.Length) dup.text = text;   //留更完整的那条
                if (m_LogBubbles)
                    Debug.Log($"[Bubble] 又提起(第{dup.touches + 1}次) heat={dup.heat:F2}: {dup.text}");
                return;
            }

            m_Bubbles.Add(new Bubble
            {
                text = text,
                fromUser = fromUser,
                heat = heat,
                lastTouch = now,
                touches = 0,
            });
            if (m_LogBubbles) Debug.Log($"[Bubble] 新增 heat={heat:F2}: {text}");
            Prune(now);
        }

        /// <summary>删除凉透的；仍然超容量时丢最凉的那个。</summary>
        public void Prune(float now)
        {
            for (int i = m_Bubbles.Count - 1; i >= 0; i--)
            {
                if (HeatOf(m_Bubbles[i], now) >= m_ForgetBelow) continue;
                if (m_LogBubbles) Debug.Log($"[Bubble] 消失: {m_Bubbles[i].text}");
                m_Bubbles.RemoveAt(i);
            }
            while (m_Bubbles.Count > m_Capacity)
            {
                int coldest = 0;
                float lo = float.MaxValue;
                for (int i = 0; i < m_Bubbles.Count; i++)
                {
                    float h = HeatOf(m_Bubbles[i], now);
                    if (h < lo) { lo = h; coldest = i; }
                }
                m_Bubbles.RemoveAt(coldest);
            }
        }

        /// <summary>
        /// 渲染气泡段落，接在长期图后面。返回空串表示没有可显示的气泡。
        /// </summary>
        public string Render(float now)
        {
            if (!m_Enabled) return string.Empty;
            Prune(now);
            if (m_Bubbles.Count == 0 || m_MaxShown <= 0) return string.Empty;

            var order = new List<Bubble>(m_Bubbles);
            order.Sort((a, b) => HeatOf(b, now).CompareTo(HeatOf(a, now)));
            int take = Mathf.Min(order.Count, m_MaxShown);

            var sb = new StringBuilder();
            //末尾那句"不要复述"是必要的：这段是对用户发言的**第三人称复述**，
            //她读完紧接着就要开口。实测曾诱导出 "彼、日本語なら気楽に話せるようだね"
            //这类第三人称反思被当成正文念出来(一场 6 句)。主因已经通过把记忆块移到
            //用户消息之前解决，这句是双保险。
            sb.Append("\n\n刚才这段对话 (这是你自己的备忘，用户看不到，也不要复述或评论它。" +
                      "气泡会慢慢消失；里面若有值得长期留下的，用 <memory_add/> 收进上面那张图):");
            for (int i = 0; i < take; i++)
            {
                var b = order[i];
                float h = HeatOf(b, now);
                //大小就是热度——草图里的"气泡"
                string mark = h > 0.7f ? "◉" : (h > 0.4f ? "◎" : "◦");
                sb.Append("\n  ").Append(mark).Append(' ')
                  .Append(b.text.Length > m_TextChars ? b.text.Substring(0, m_TextChars) : b.text)
                  .Append("  (").Append(Ago(now - b.lastTouch));
                if (b.touches > 0) sb.Append("，提起过 ").Append(b.touches + 1).Append(" 次");
                sb.Append(')');
            }
            if (order.Count > take) sb.Append("\n  …还有 ").Append(order.Count - take).Append(" 条更淡的");
            return sb.ToString();
        }

        private Bubble FindSimilar(string text)
        {
            for (int i = 0; i < m_Bubbles.Count; i++)
            {
                string a = m_Bubbles[i].text;
                if (a == text) return m_Bubbles[i];
                //一方包含另一方(用户把同一句话说完整了/复述了)算同一件事
                if (a.Length >= 6 && text.Length >= 6 &&
                    (a.IndexOf(text, StringComparison.Ordinal) >= 0 ||
                     text.IndexOf(a, StringComparison.Ordinal) >= 0))
                    return m_Bubbles[i];
            }
            return null;
        }

        private static string Ago(float sec)
        {
            if (sec < 45f) return "刚刚";
            if (sec < 3600f) return Mathf.RoundToInt(sec / 60f) + "分钟前";
            return (sec / 3600f).ToString("F1") + "小时前";
        }

        /// <summary>
        /// 去掉 ASR 加的方括号元数据前缀。不去掉的话气泡里会存着
        /// 「[说话人:主人; speaker_id:owner…]」这种东西——实测她曾把这类标注文本
        /// 当成内容用出去过(拿「ASR推测的」当歌名发起了检索)。
        /// </summary>
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            while (s.StartsWith("[", StringComparison.Ordinal))
            {
                int close = s.IndexOf(']');
                if (close < 0) break;
                s = s.Substring(close + 1).TrimStart();
            }
            return s.Trim();
        }
    }
}
