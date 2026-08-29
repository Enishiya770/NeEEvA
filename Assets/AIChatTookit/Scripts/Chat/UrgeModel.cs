using System;
using UnityEngine;

namespace AIChat.Agent
{
    /// <summary>
    /// 「想开口」的冲动累积器——取代固定倒计时的 tick 调度。
    ///
    /// 原来的设计是：轮次结束时读 LLM 的 &lt;next in="Ns"/&gt;，起一个 N 秒的协程，到点就发。
    /// 问题不在"固定"(间隔本来就是她自己排的)，而在这三件事:
    ///   1. 一次决定、然后盲等。等待期间世界发生什么都改不了它——每多一种"该让她提前
    ///      醒"的事件就得多打一个补丁(环境 spike 拉前、工具结果拉前……)。
    ///   2. 闹钟响了就必须推理，哪怕什么也没发生。
    ///   3. 防独白靠硬上限，撞到就彻底闭嘴等用户开口——是断崖，不是疲劳。
    ///
    /// 改成能量式之后:
    ///   U 每帧累积，越过阈值就发一次 tick。&lt;next in/&gt; 的语义从"定时"变成"定速"——
    ///   速率按剩余缺口现算，使得**无事发生时恰好 N 秒后触顶**。她的意图完整保留。
    ///   其余所有输入(孤独、记忆、环境)都是**加法**项，只能让她比自己排的更早开口；
    ///   想更晚只有疲劳一条路，而那是有主体依据的——没人理我，那我少说点。
    ///
    /// 参数是拿真实 Editor.log 重建的会话 + 真实 memory.json 跑模拟定的，别凭感觉调:
    ///   - 回放 2026-08-03 那段 5 分钟会话: 实际主动开口 6 次，本模型 7 次。多出的一次
    ///     有账可算——当前设计把 11/16 个环境 spike 当成"本段沉默已拉前过"丢掉了，
    ///     这些加起来正好是 1.15 个阈值。
    ///   - 用户离开后的 30 分钟: 本模型开口 13 次、间隔从 78s 自己拉长到 178s；
    ///     当前设计是固定 60s 一次，第 8 次撞上硬上限后 22 分钟一声不吭。
    ///
    /// 注意: 本模型决定的是**她什么时候醒来看一眼**，不是什么时候说话——
    /// &lt;silent/&gt; 仍是第二道闸。所以每次触发都要付一次 LLM 推理，触发率就是成本。
    /// </summary>
    [Serializable]
    public class UrgeModel
    {
        [Tooltip("关掉则退回原来的固定倒计时调度(ChatSample 里仍保留那条通路)")]
        [SerializeField] private bool m_Enabled = true;

        [Tooltip("孤独滴流的满速(每秒)。静默越久她越想说话——这一项让'没人理'本身成为动机。" +
                 "0.0030/s 意味着光靠孤独要 333 秒才能独立触顶，所以它只是加速，不是主因")]
        [Range(0f, 0.02f)] [SerializeField] private float m_LonelyRate = 0.0030f;

        [Tooltip("距用户上次开口这么久之后，孤独滴流到满速(之前按比例)")]
        [Range(30f, 900f)] [SerializeField] private float m_LonelyFullSec = 240f;

        [Tooltip("「忽然想起一件事」推多少冲动，乘在待机漂移注入的能量上(默认 0.60，" +
                 "所以一次漂移约 +0.24，相当于一个中等环境动静)。\n" +
                 "**这里必须是脉冲，不能是'激活水平'的连续项**——早先的版本拿 " +
                 "MemoryHub.TotalActivation() 当连续输入，实测炸了: 每次发言(含她自己的)都会做" +
                 "名字提及扫描，命中 +1.0 再扩散两跳，而'アントネーワ''中央庭'这类节点名她" +
                 "几乎每句都说，于是 25 个节点长期顶在上限 1.5 附近、180s 半衰期排不掉。\n" +
                 "结果是这个量在对话期间恒定饱和在 12~28(我按 0.5 标定，差了 50 倍)，" +
                 "记忆项占了每次触发的 67~96%，触发率涨到 3 倍(15.4s 一次)，首 token 中位" +
                 "从 0.8s 退到 1.33s；更讽刺的是待机漂移因此再没跑起来过——她平均 15 秒就说一次话，" +
                 "凑不满 45 秒静默。\n" +
                 "「她想起某件事」本来就是**事件**不是水平，而漂移只在静默期发生，" +
                 "正好只在该起作用的时候起作用")]
        [Range(0f, 2f)] [SerializeField] private float m_MemorySurfacingGain = 0.40f;

        [Tooltip("单次环境 spike 的冲动增益。原设计是二值的——rms 0.0126 和 0.0234 一样都直接" +
                 "砍到 1s，而且一段沉默只认第一次，之后全丢。实测某段 5 分钟会话丢掉了 11/16 个 " +
                 "spike。这里改成按响度给、可累加")]
        [Range(0f, 1f)] [SerializeField] private float m_SpikeGain = 0.25f;

        [Tooltip("低于这个 rms 不算动静，对齐 RTSpeechHandler 的 m_RmsSpikeThreshold")]
        [SerializeField] private float m_SpikeFloorRms = 0.012f;

        [Tooltip("**一段沉默里环境最多能贡献多少冲动**。必须小于阈值 1.0，否则风扇/施工这类" +
                 "持续噪音能单独把她驱动起来——那正是原来那道一次性闸要防的事(她会每 10 秒" +
                 "醒一次、面对完全相同的情境连说数遍几乎一样的话)。用户开口后清零")]
        [Range(0f, 1f)] [SerializeField] private float m_SpikeMaxPerSilence = 0.60f;

        [Tooltip("每连续一次说话没等到用户回应，她自己排的间隔就拉长这么多倍(0.45 = 第 n 次" +
                 "变成 1+0.45n 倍)。这是'疲劳'——没人理我，那我少说点。\n" +
                 "疲劳必须作用在**间隔**上：早先的版本改成抬阈值，结果完全无效——阈值涨了，" +
                 "但开口消耗是定值，残余 U 跟着同步上涨，缺口恒定，连续第 10 次和第 1 次" +
                 "的间隔一模一样。\n" +
                 "拿 <next in 60s> 跑 30 分钟静默: 间隔从 78s 自己拉长到 178s，共 13 次；" +
                 "原设计是固定 60s、第 8 次撞上硬上限后 22 分钟一声不吭")]
        [Range(0f, 2f)] [SerializeField] private float m_FatiguePerTurn = 0.45f;

        [Tooltip("打印每次触发时的冲动构成——排障时这是唯一能看懂'她为什么这时候说话'的东西")]
        [SerializeField] private bool m_LogUrge = true;

        private const float Threshold = 1.0f;   //恒定。用"抬阈值"表达疲劳是无效的，见 SetNextIn 注释

        private float m_U;
        private float m_Rate;              //由 SetNextIn 现算，使无事发生时恰好 N 秒后触顶
        private float m_LastUserTime;
        private float m_RequestedSec;      //她自己排的原始值，仅供日志
        private float m_EffectiveSec;      //施加疲劳后的值，仅供日志

        //本次触发累计到的各分量，只为日志好读
        private float m_AccBase, m_AccLonely, m_AccMemory, m_AccSpike;
        private float m_SpikeSpentThisSilence;   //本段沉默里环境已经贡献掉的额度

        private string m_LastCause = "clock";

        public bool Enabled { get { return m_Enabled; } }
        public float Value { get { return m_U; } }
        public float EffectiveSec { get { return m_EffectiveSec; } }
        /// <summary>上一次触发的主因: clock / spike / memory。用来给感知帧写实话。</summary>
        public string LastCause { get { return m_LastCause; } }

        /// <summary>Agent Loop 启动/停止时调用，清空全部状态。</summary>
        public void Reset(float now)
        {
            m_U = 0f;
            m_Rate = 0f;
            m_LastUserTime = now;
            m_RequestedSec = m_EffectiveSec = 0f;
            m_SpikeSpentThisSilence = 0f;
            m_AccBase = m_AccLonely = m_AccMemory = m_AccSpike = 0f;
        }

        /// <summary>
        /// 丢掉已经排好的旧点火，但保留“用户沉默了多久”和本段环境额度。
        /// 异步工具确认或内部意图预判接管当前节奏时使用；若调用 Reset，角色会把
        /// 一次内部流程误当成用户刚刚开口，孤独时间也被不自然地清零。
        /// </summary>
        public void ClearPendingTrigger()
        {
            m_U = 0f;
            m_Rate = 0f;
            m_RequestedSec = m_EffectiveSec = 0f;
            m_AccBase = m_AccLonely = m_AccMemory = m_AccSpike = 0f;
        }

        /// <summary>
        /// 相当于原来的 ScheduleNextTick：把"N 秒后再醒"翻译成一个速率。
        ///
        /// 速率按**剩余缺口**现算(而不是固定值)，这样无论 U 当前是多少，无事发生时都恰好
        /// N 秒后触顶。fatigueTurns 会拉长 N——疲劳必须作用在间隔上，不能靠抬阈值:
        /// 抬阈值时开口消耗如果是定值，残余 U 会跟着阈值同步上涨，缺口恒定，等于没抬。
        /// </summary>
        public void SetNextIn(float requestedSec, int fatigueTurns, float minSec, float maxSec)
        {
            m_RequestedSec = requestedSec;
            float stretched = requestedSec * (1f + m_FatiguePerTurn * Mathf.Max(0, fatigueTurns));
            m_EffectiveSec = Mathf.Clamp(stretched, minSec, maxSec);
            m_Rate = Mathf.Max(0f, Threshold - m_U) / m_EffectiveSec;
            m_AccBase = m_AccLonely = m_AccMemory = m_AccSpike = 0f;
        }

        /// <summary>用户开口——这次交流把冲动吸收掉了，环境额度也重新放开。</summary>
        public void AbsorbUserUtterance(float now)
        {
            m_U = 0f;
            m_LastUserTime = now;
            m_SpikeSpentThisSilence = 0f;
            m_AccBase = m_AccLonely = m_AccMemory = m_AccSpike = 0f;
        }

        /// <summary>
        /// 环境出现非语音动静(咳嗽、翻身、键盘)。按响度给冲动，可累加，但整段沉默有总额度。
        /// 返回实际注入的量(被额度截断时会小于名义值，0 表示额度已用完)。
        /// </summary>
        public float AddSpike(float peakRms)
        {
            if (!m_Enabled) return 0f;
            float over = (peakRms - m_SpikeFloorRms) / Mathf.Max(0.0001f, m_SpikeFloorRms);
            float nominal = m_SpikeGain * Mathf.Clamp(over, 0.2f, 2f);
            float room = Mathf.Max(0f, m_SpikeMaxPerSilence - m_SpikeSpentThisSilence);
            float actual = Mathf.Min(nominal, room);
            if (actual <= 0f) return 0f;
            m_SpikeSpentThisSilence += actual;
            m_U += actual;
            m_AccSpike += actual;
            return actual;
        }

        /// <summary>
        /// 待机漂移点火——她忽然想起了什么。driftEnergy 是 MemoryHub 注入的激活能量。
        /// </summary>
        public void AddMemorySurfacing(float driftEnergy)
        {
            if (!m_Enabled || driftEnergy <= 0f) return;
            float v = m_MemorySurfacingGain * driftEnergy;
            m_U += v;
            m_AccMemory += v;
        }

        /// <summary>
        /// 推进一帧。返回 true 表示该发 tick 了。
        /// 调用方负责在"轮次在飞/她正在说话"时不要调用本方法——那时冲动正在被消耗。
        /// </summary>
        public bool Step(float dt, float now)
        {
            if (!m_Enabled || dt <= 0f) return false;

            float b = m_Rate * dt;
            float l = m_LonelyRate * Mathf.Clamp01((now - m_LastUserTime) / Mathf.Max(1f, m_LonelyFullSec)) * dt;

            m_U += b + l;
            m_AccBase += b; m_AccLonely += l;

            if (m_U < Threshold) return false;

            //哪一项占了大头——要如实告诉她这一帧是怎么来的，不能一律说成"你自己的钟到点了"
            float clock = m_AccBase + m_AccLonely;
            m_LastCause = (m_AccSpike >= clock && m_AccSpike >= m_AccMemory) ? "spike"
                        : (m_AccMemory >= clock) ? "memory"
                        : "clock";

            if (m_LogUrge)
            {
                Debug.Log($"[Urge] 触发 U={m_U:F2} 主因={m_LastCause} — 她排的 {m_RequestedSec:F0}s→" +
                          $"实际 {m_EffectiveSec:F0}s；构成: 底噪 {m_AccBase:F2} / 孤独 {m_AccLonely:F2}" +
                          $" / 记忆 {m_AccMemory:F2} / 环境 {m_AccSpike:F2}" +
                          $"  (环境额度已用 {m_SpikeSpentThisSilence:F2}/{m_SpikeMaxPerSilence:F2})");
            }
            m_U = Mathf.Max(0f, m_U - Threshold);   //只结转溢出部分
            m_AccBase = m_AccLonely = m_AccMemory = m_AccSpike = 0f;
            return true;
        }
    }
}
