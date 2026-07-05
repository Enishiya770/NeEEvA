using System;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆网络的节点。客观事实，不预存情绪——情绪是 LLM 读取节点时的活生生感受。
    ///
    /// 字段对应文档 3.1：
    ///   name             节点名称(简洁唯一,作为身份键)
    ///   description      客观描述——LLM 用它做联想推理与回答,不是搜索关键字
    ///   weight           [0,1] 权重,决定节点在记忆库里的核心程度(影响是否进入 top-N)
    ///   last_activated   ISO 8601 字符串,最后被激活时间(LLM 显式提及/写入算激活)
    ///
    /// 注: connections 不在此类——边作为独立的 MemoryEdge 列表存储。
    /// 注: 设计上不再有 aliases——语义关联交给 LLM 自己做(它已有这种能力),
    ///     工程层只把节点放到她视野里,不替它做"挑选"。
    /// </summary>
    [Serializable]
    public class MemoryNode
    {
        public string name;
        public string description;
        public float weight;
        public string last_activated;

        public MemoryNode() { }

        public MemoryNode(string name, string description, float weight)
        {
            this.name = name;
            this.description = description;
            this.weight = weight;
            this.last_activated = DateTime.UtcNow.ToString("o");
        }

        /// <summary>
        /// 把 last_activated 字符串解析为 DateTime(UTC)。失败返回 DateTime.MinValue。
        /// 注: 不能 RoundtripKind | AssumeUniversal 同时用——.NET 禁止;
        /// 只用 RoundtripKind 即可,因为种子串里的 "Z" 后缀已经把 Kind=Utc 标好了。
        /// </summary>
        public DateTime GetLastActivatedUtc()
        {
            DateTime t;
            if (DateTime.TryParse(last_activated, null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out t))
            {
                return t.ToUniversalTime();
            }
            return DateTime.MinValue;
        }

        public void TouchActivated()
        {
            last_activated = DateTime.UtcNow.ToString("o");
        }
    }
}
