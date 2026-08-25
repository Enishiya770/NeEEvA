using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIChat.Memory
{
    /// <summary>
    /// LLM 记忆写入标签的解析与剥离。
    ///
    /// 支持两个自闭合标签(属性顺序任意、均可省略,desc 内不要出现双引号/句号/换行):
    ///   &lt;memory_add name="..." desc="..." weight="0.7"/&gt;    新增记忆节点
    ///   &lt;memory_update name="..." desc="..." weight="0.8"/&gt; 更新已有节点并刷新激活时间
    ///
    /// 与 next/continue 等标签同为"放在回复末尾"的约定。
    /// Extract 取出操作并返回剥净的文本(给 OnStreamComplete 的全文解析用);
    /// Strip 只剥不取(给 TTS 剥离路径用,一次流式回复只能应用一次操作,不能在 chunk 级重复提取)。
    /// </summary>
    public static class MemoryTagParser
    {
        public struct MemoryOp
        {
            public bool isUpdate;   //false=add, true=update（link 操作忽略此字段）
            public bool isLink;     //true = 写边，此时用 from/to/strength，不用 name/desc
            public bool isNote;     //true = 短期气泡，此时只用 desc 当正文
            public string name;
            public string desc;     //null = 未提供
            public float weight;    //仅 hasWeight 时有效
            public bool hasWeight;
            public string from;     //isLink 时的边起点
            public string to;       //isLink 时的边终点
            public float strength;  //isLink 时的边强度，<=0 表示删除该边
            public bool hasStrength;
        }

        //属性顺序任意: name= / desc= / weight= 循环匹配(与 ChatSample 里 <next/> 的写法一致)
        static readonly Regex s_AddRegex = new Regex(
            @"<memory_add(?:\s+(?:name=""(?<name>[^""]*)""|desc=""(?<desc>[^""]*)""|weight=""(?<weight>[^""]*)""))*\s*/>",
            RegexOptions.IgnoreCase);
        static readonly Regex s_UpdateRegex = new Regex(
            @"<memory_update(?:\s+(?:name=""(?<name>[^""]*)""|desc=""(?<desc>[^""]*)""|weight=""(?<weight>[^""]*)""))*\s*/>",
            RegexOptions.IgnoreCase);
        //写边：<memory_link from="A" to="B" strength="0.7"/>，strength="0" 表示断开
        static readonly Regex s_LinkRegex = new Regex(
            @"<memory_link(?:\s+(?:from=""(?<from>[^""]*)""|to=""(?<to>[^""]*)""|strength=""(?<strength>[^""]*)""))*\s*/>",
            RegexOptions.IgnoreCase);
        //短期气泡：<note text="..."/>。不进长期图，会自己消失——用于"想记着但还不确定
        //要不要长期留下"的事。注意它不以 <memory_ 开头，下面两处快速路径判断要一起放行。
        static readonly Regex s_NoteRegex = new Regex(
            @"<note(?:\s+text=""(?<text>[^""]*)"")?\s*/>",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// 从文本中提取全部记忆操作并剥掉标签。没有记忆标签时返回 null 且文本原样带回。
        /// </summary>
        public static List<MemoryOp> Extract(string text, out string cleanText)
        {
            cleanText = text ?? "";
            if (string.IsNullOrEmpty(text) || !HasAnyTag(text))
                return null;

            var ops = new List<MemoryOp>();
            cleanText = s_AddRegex.Replace(cleanText, m => { Collect(m, false, ops); return ""; });
            cleanText = s_UpdateRegex.Replace(cleanText, m => { Collect(m, true, ops); return ""; });
            cleanText = s_LinkRegex.Replace(cleanText, m => { CollectLink(m, ops); return ""; });
            cleanText = s_NoteRegex.Replace(cleanText, m => { CollectNote(m, ops); return ""; });
            cleanText = cleanText.Trim();
            return ops.Count > 0 ? ops : null;
        }

        /// <summary>只剥标签不取内容。无标签时零成本原样返回。</summary>
        public static string Strip(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasAnyTag(text))
                return text;
            text = s_AddRegex.Replace(text, "");
            text = s_UpdateRegex.Replace(text, "");
            text = s_LinkRegex.Replace(text, "");
            text = s_NoteRegex.Replace(text, "");
            return text.Trim();
        }

        static bool HasAnyTag(string text)
        {
            return text.IndexOf("<memory_", System.StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("<note", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void CollectNote(Match m, List<MemoryOp> ops)
        {
            var op = new MemoryOp();
            op.isNote = true;
            op.desc = m.Groups["text"].Success ? m.Groups["text"].Value.Trim() : null;
            if (!string.IsNullOrEmpty(op.desc)) ops.Add(op);
        }

        static void Collect(Match m, bool isUpdate, List<MemoryOp> ops)
        {
            var op = new MemoryOp();
            op.isUpdate = isUpdate;
            op.name = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : null;
            op.desc = m.Groups["desc"].Success ? m.Groups["desc"].Value.Trim() : null;
            if (m.Groups["weight"].Success)
            {
                float w;
                if (float.TryParse(m.Groups["weight"].Value.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out w))
                {
                    op.weight = w;
                    op.hasWeight = true;
                }
            }
            //name 是身份键,缺了整条操作作废
            if (!string.IsNullOrEmpty(op.name)) ops.Add(op);
        }

        static void CollectLink(Match m, List<MemoryOp> ops)
        {
            var op = new MemoryOp();
            op.isLink = true;
            op.from = m.Groups["from"].Success ? m.Groups["from"].Value.Trim() : null;
            op.to = m.Groups["to"].Success ? m.Groups["to"].Value.Trim() : null;
            if (m.Groups["strength"].Success)
            {
                float st;
                if (float.TryParse(m.Groups["strength"].Value.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out st))
                {
                    op.strength = st;
                    op.hasStrength = true;
                }
            }
            //两端缺一不可；自环无意义
            if (!string.IsNullOrEmpty(op.from) && !string.IsNullOrEmpty(op.to) && op.from != op.to)
                ops.Add(op);
        }
    }
}
