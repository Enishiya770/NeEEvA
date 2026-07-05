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
            public bool isUpdate;   //false=add, true=update
            public string name;
            public string desc;     //null = 未提供
            public float weight;    //仅 hasWeight 时有效
            public bool hasWeight;
        }

        //属性顺序任意: name= / desc= / weight= 循环匹配(与 ChatSample 里 <next/> 的写法一致)
        static readonly Regex s_AddRegex = new Regex(
            @"<memory_add(?:\s+(?:name=""(?<name>[^""]*)""|desc=""(?<desc>[^""]*)""|weight=""(?<weight>[^""]*)""))*\s*/>",
            RegexOptions.IgnoreCase);
        static readonly Regex s_UpdateRegex = new Regex(
            @"<memory_update(?:\s+(?:name=""(?<name>[^""]*)""|desc=""(?<desc>[^""]*)""|weight=""(?<weight>[^""]*)""))*\s*/>",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// 从文本中提取全部记忆操作并剥掉标签。没有记忆标签时返回 null 且文本原样带回。
        /// </summary>
        public static List<MemoryOp> Extract(string text, out string cleanText)
        {
            cleanText = text ?? "";
            if (string.IsNullOrEmpty(text) ||
                text.IndexOf("<memory_", System.StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            var ops = new List<MemoryOp>();
            cleanText = s_AddRegex.Replace(cleanText, m => { Collect(m, false, ops); return ""; });
            cleanText = s_UpdateRegex.Replace(cleanText, m => { Collect(m, true, ops); return ""; });
            cleanText = cleanText.Trim();
            return ops.Count > 0 ? ops : null;
        }

        /// <summary>只剥标签不取内容。无标签时零成本原样返回。</summary>
        public static string Strip(string text)
        {
            if (string.IsNullOrEmpty(text) ||
                text.IndexOf("<memory_", System.StringComparison.OrdinalIgnoreCase) < 0)
                return text;
            text = s_AddRegex.Replace(text, "");
            text = s_UpdateRegex.Replace(text, "");
            return text.Trim();
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
    }
}
