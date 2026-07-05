using System.Collections.Generic;
using UnityEngine;

namespace AIChat.Memory
{
    /// <summary>
    /// 记忆排序——给定 store,返回按重要性排序的 top-N 节点。
    ///
    /// 设计取舍(2025 版改写):
    /// 之前这里实现的是 "用户 query → 关键词/n-gram/aliases 命中 → top-K 召回"。
    /// 那条路在用 字符串匹配 模拟 语义相似度,注定漏掉"生还/活着/幸存"这种同义关联。
    /// 改为: 工程层不替 LLM 挑选,只把核心节点全部放进感知帧,
    /// 由 LLM 用它原本就具备的语言知识在生成时做语义关联。
    ///
    /// 排序逻辑:
    ///   importance = weight × recencyBoost
    ///     weight       — 节点的核心程度(LLM 写入时设, 衰减时变低)
    ///     recencyBoost — 1 + 0.5 × exp(-Δt/7天),最近被显式激活的稍微浮上来
    ///
    /// 取 top-N 后按 weight 降序输出(稳定可读)。
    /// </summary>
    public static class MemoryRanking
    {
        public static List<MemoryNode> SelectTop(MemoryStore store, int n)
        {
            if (store == null || n <= 0) return new List<MemoryNode>();
            var nodes = store.Nodes;
            if (nodes == null || nodes.Count == 0) return new List<MemoryNode>();

            var now = System.DateTime.UtcNow;
            var scored = new List<KeyValuePair<MemoryNode, float>>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                var nd = nodes[i];
                if (nd == null || string.IsNullOrEmpty(nd.name)) continue;

                System.DateTime t = nd.GetLastActivatedUtc();
                float days = (t == System.DateTime.MinValue) ? 365f : (float)(now - t).TotalDays;
                if (days < 0f) days = 0f;
                float recencyBoost = 1f + 0.5f * Mathf.Exp(-days / 7f);  //最近激活 → 1.5x

                float score = Mathf.Clamp01(nd.weight) * recencyBoost;
                scored.Add(new KeyValuePair<MemoryNode, float>(nd, score));
            }

            scored.Sort((a, b) => b.Value.CompareTo(a.Value));
            int take = Mathf.Min(n, scored.Count);
            var result = new List<MemoryNode>(take);
            for (int i = 0; i < take; i++) result.Add(scored[i].Key);
            return result;
        }
    }
}
