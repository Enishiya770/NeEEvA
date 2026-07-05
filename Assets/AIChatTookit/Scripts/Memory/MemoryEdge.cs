using System;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆网络的边。有向(from → to)+ 强度。强度由 LLM 自己设定,
    /// 系统层只负责按强度做召回评分加权和衰减。
    ///
    /// 设计取舍:本来可以做无向,但有向能表达"A 让我想到 B"的不对称联想——
    /// 比如"七日目"会强烈唤起"重生",但"重生"未必每次都唤起"七日目"。
    /// 召回时双向都看,只是权重不同。
    /// </summary>
    [Serializable]
    public class MemoryEdge
    {
        public string from;
        public string to;
        public float strength;

        public MemoryEdge() { }

        public MemoryEdge(string from, string to, float strength)
        {
            this.from = from;
            this.to = to;
            this.strength = strength;
        }
    }
}
