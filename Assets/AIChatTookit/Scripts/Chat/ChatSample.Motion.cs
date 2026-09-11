using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public partial class ChatSample
{
    /// <summary>Only completed, validated role actions reach this event; never raw model output.</summary>
    public event Action<DialogueMotionIntent> MotionIntentRequested;
    public event Action<int, string> MotionCancelled;
    /// <summary>Read-only facts from the bound motion controller; never a new action command.</summary>
    public event Func<string> MotionStateContextRequested;

    private bool m_MotionOutputEnabled;
    private bool m_GeneratedMotionEnabled;
    [SerializeField, Tooltip("实验性语义动作规划。尚有约束理解遗漏；默认保留已验收的动作协议。")]
    private bool m_UseSemanticMotionPlanning;
    private bool m_ConfiguredSemanticMotionPlanning;

    public bool SemanticMotionPlanningEnabled => m_UseSemanticMotionPlanning;

    public void ConfigureSemanticMotionPlanning(bool enabled)
    {
        m_UseSemanticMotionPlanning = enabled;
        if (m_MotionOutputEnabled) ConfigureMotionOutput(true, m_GeneratedMotionEnabled);
        else m_ConfiguredSemanticMotionPlanning = enabled;
    }
    private bool m_ReadingUserInput;
    private int m_MotionSequence;
    private int m_MotionDispatchedGeneration = int.MinValue;
    private readonly HashSet<string> m_MotionDispatchedKeys = new HashSet<string>(StringComparer.Ordinal);
    [Serializable] private sealed class MotionProtocolRejectionFact
    {
        public string source = "dialogue-motion-parser", status = "rejected-before-dispatch";
        public int responseGeneration, sequence;
        public string reason;
    }
    [Serializable] private sealed class MotionProtocolFeedbackFact
    {
        public int currentResponseGeneration;
        public MotionProtocolRejectionFact lastMotionProtocolRejection;
    }
    private MotionProtocolRejectionFact m_LastMotionProtocolRejection;

    private int BeginFormalResponseGeneration()
    {
        if (m_ConfiguredSemanticMotionPlanning != m_UseSemanticMotionPlanning)
            ConfigureMotionOutput(m_MotionOutputEnabled, m_GeneratedMotionEnabled);
        // Speech generations reject stale model callbacks. A committed body action
        // has its own lifetime, and may continue through a new autonomous speech frame.
        int generation = ++m_FormalResponseGeneration;
        BeginMotionPlanningResponse();
        BeginMotionFeedbackResponse();
        m_MotionDispatchedGeneration = generation;
        m_MotionDispatchedKeys.Clear();
        return generation;
    }

    public void ConfigureMotionOutput(bool enabled, bool allowGenerated)
    {
        // Disconnect/rebind uses disabled -> enabled. A parser rejection belongs to
        // that binding, never to a later avatar or a disabled action channel.
        bool modeChanged = m_ConfiguredSemanticMotionPlanning != m_UseSemanticMotionPlanning;
        if (!enabled || modeChanged || m_MotionOutputEnabled != enabled || m_GeneratedMotionEnabled != (enabled && allowGenerated))
        {
            m_LastMotionProtocolRejection = null;
            ResetActionPlanning();
            ResetMotionFeedback();
        }
        if (m_MotionOutputEnabled && !enabled) CancelDialogueMotion("motion-output-disabled");
        else if (m_MotionOutputEnabled && modeChanged) CancelDialogueMotion("motion-planning-mode-changed");
        m_ConfiguredSemanticMotionPlanning = m_UseSemanticMotionPlanning;
        m_MotionOutputEnabled = enabled;
        m_GeneratedMotionEnabled = enabled && allowGenerated;
    }

    private string BuildMotionRequestContext(string context)
    {
        if (m_AgentRunning) context = (context ?? "") + SelfInspectionContract;
        if (m_AgentRunning && (GetSkillRouteState("singing").ActiveThisRound || HasPendingSingingGoalReview()))
            context += SingingGoalContract;
        if (m_ConfiguredSemanticMotionPlanning != m_UseSemanticMotionPlanning)
            ConfigureMotionOutput(m_MotionOutputEnabled, m_GeneratedMotionEnabled);
        if (!m_MotionOutputEnabled || MotionIntentRequested == null) return context ?? "";
        string contract = m_GeneratedMotionEnabled
            ? (m_UseSemanticMotionPlanning ? DialogueMotionProtocol.SemanticOutputContract : DialogueMotionProtocol.GeneratedOutputContract)
            : DialogueMotionProtocol.BasicOutputContract;
        if (m_GeneratedMotionEnabled) contract += "\n" + DialogueMotionProtocol.MotionFeedbackOutputContract;
        contract += "\n一次 motion 会独立执行，不会随着下一句话自动重新开始。" +
            "continue/next 只调度后续回复，不表示身体动作已经完成；不要为同一动作在续轮中重复发送 motion。" +
            "已有动作进行中时可以继续说话；只有确实要改变动作时才发送新 motion，停止用 none。";
        var sources = MotionStateContextRequested;
        if (sources != null)
        {
            var facts = new StringBuilder();
            foreach (Func<string> source in sources.GetInvocationList())
            {
                try
                {
                    string state = source();
                    if (!string.IsNullOrWhiteSpace(state)) facts.AppendLine(state.Trim());
                }
                catch (Exception error) { Debug.LogException(error, this); }
            }
            if (facts.Length > 0) contract += "\n[当前身体动作；程序事实]\n" + facts +
                "lastRequested 只记录上次请求，不能当作已达到或仍在保持的姿态。" +
                "idle-Animator 表示已回待机；用户要求在此前提下继续时，保留其未撤销的动作约束，" +
                "在新指令中完整描述准备姿态与局部动作，不要假称上一姿态仍在保持。" +
                "只有程序事实明确为holding且previousRequestedPoseHeld=true时，才能以compose的current继续实际持姿。";
            if (facts.Length > 0) contract +=
                "lastControlPlan/lastControlObservation是上次控制尝试的记录，不表示当前仍保持。" +
                "lastControlError非空表示执行拒绝或未达标；不要复述成已完成，也不要原样重复不可达计划。" +
                "根据反馈调整用户未明确限定的姿态自由度；必须保留明确约束，冲突无法解决时说明限制。";
        }
        if (m_LastMotionProtocolRejection != null)
            contract += "\n[最近动作协议拒绝；历史解析事实，不是当前身体状态]\n" +
                JsonUtility.ToJson(new MotionProtocolFeedbackFact {
                    currentResponseGeneration = m_FormalResponseGeneration,
                    lastMotionProtocolRejection = m_LastMotionProtocolRejection });
        if (m_GeneratedMotionEnabled && m_UseSemanticMotionPlanning) contract += BuildActionPlanningFacts();
        if (m_LastMotionExecutionFeedback != null)
            contract += "\n[最近动作执行反馈；历史观测，不是当前仍在持姿]\n" + MotionFeedbackJson(m_LastMotionExecutionFeedback);
        if (m_LastMotionRepairResult != null)
            contract += "\n[最近动作修订结果；未得到新动作或公开说明]\n" + JsonUtility.ToJson(m_LastMotionRepairResult);
        return string.IsNullOrWhiteSpace(context) ? contract : context.TrimEnd() + "\n\n" + contract;
    }

    private DialogueMotionIntent? ExtractDialogueMotion(ref string text)
    {
        int sequence = ++m_MotionSequence;
        string executable = text;
        if (DialogueMotionProtocol.TryExtract(ref text, m_FormalResponseGeneration, sequence,
            out DialogueMotionIntent intent, out string rejection))
        {
            // A later valid role command replaces parser feedback. Execution failures
            // are independently recorded by the controller as lastControlError.
            if (m_MotionOutputEnabled && !m_ReadingUserInput) m_LastMotionProtocolRejection = null;
            return intent;
        }
        if (!string.IsNullOrEmpty(rejection))
        {
            if (m_MotionOutputEnabled && !m_ReadingUserInput)
                m_LastMotionProtocolRejection = new MotionProtocolRejectionFact {
                    responseGeneration = m_FormalResponseGeneration, sequence = sequence, reason = rejection };
            Debug.LogWarning("[Dialogue/Motion] " + rejection, this);
            RejectDialogueMotion(null, executable, rejection, sequence);
        }
        // Ordinary speech with no command leaves the latest rejection available for
        // the next request; its original generation identifies it as historical.
        return null;
    }

    private void DispatchDialogueMotion(DialogueMotionIntent? pending, bool repeated)
    {
        if (m_ConfiguredSemanticMotionPlanning != m_UseSemanticMotionPlanning)
            ConfigureMotionOutput(m_MotionOutputEnabled, m_GeneratedMotionEnabled);
        if (!pending.HasValue || !m_MotionOutputEnabled || m_ReadingUserInput || repeated ||
            m_RoundSilencedForRepeat || m_UserSpeechActiveForAutonomy) return;
        DialogueMotionIntent intent = pending.Value;
        if (intent.ResponseGeneration != m_FormalResponseGeneration) return;
        if ((intent.Name == "generate" || intent.Name == "compose" || intent.Name == "plan" || intent.Name == "replay") && !m_GeneratedMotionEnabled)
        {
            RejectDialogueMotion(intent, "", "当前仅启用基本手势，该动作能力未开启；请说明限制，不能声称已执行。", intent.Sequence);
            return;
        }
        if (intent.Name == "plan" && !m_UseSemanticMotionPlanning)
        {
            RejectDialogueMotion(intent, "", "实验语义plan未开启，请使用当前已启用的动作协议并保留原意。", intent.Sequence);
            return;
        }
        // A motion listener failure must never stop speech, history, or another tool.
        var listeners = MotionIntentRequested;
        if (listeners == null) return;
        if (m_MotionDispatchedGeneration != intent.ResponseGeneration)
        {
            m_MotionDispatchedGeneration = intent.ResponseGeneration;
            m_MotionDispatchedKeys.Clear();
        }
        string key = intent.Name + "\n" + (intent.ActionPlan != null ? JsonUtility.ToJson(intent.ActionPlan)
            : intent.ControlPlan != null ? JsonUtility.ToJson(intent.ControlPlan) : intent.Description) +
            "\n" + intent.ReplayReference + "\n" + (intent.Goal == null ? "null" : JsonUtility.ToJson(intent.Goal));
        if (intent.Name == "none") m_MotionDispatchedKeys.Clear();
        else if (m_MotionDispatchedKeys.Contains(key))
        {
            if (m_LogAgentLoop) Debug.Log($"[Dialogue/Motion] 同一回复链的相同动作已提交，保留正在执行的动作；generation={intent.ResponseGeneration}, name={intent.Name}", this);
            return;
        }
        if (!TryPreserveMotionRepairTarget(ref intent, out string repairError))
        {
            RejectDialogueMotion(intent, "", repairError, intent.Sequence);
            return;
        }
        if (!TryResolveActionPlan(ref intent))
        {
            RejectDialogueMotion(intent, "", m_LastActionPlanError, intent.Sequence);
            return;
        }
        RegisterMotionAction(ref intent);
        if (intent.Name != "none") m_MotionDispatchedKeys.Add(key);
        if (m_LogAgentLoop) Debug.Log($"[Dialogue/Motion] dispatch t={Time.realtimeSinceStartup:F3}, generation={intent.ResponseGeneration}, sequence={intent.Sequence}, name={intent.Name}", this);
        foreach (Action<DialogueMotionIntent> listener in listeners.GetInvocationList())
        {
            try { listener(intent); }
            catch (Exception error) { Debug.LogException(error, this); }
        }
    }

    private void CancelDialogueMotion(string reason)
    {
        m_MotionDispatchedKeys.Clear();
        ResetMotionFeedback();
        var listeners = MotionCancelled;
        if (listeners == null) return;
        if (m_LogAgentLoop) Debug.Log($"[Dialogue/Motion] cancel t={Time.realtimeSinceStartup:F3}, generation={m_FormalResponseGeneration}, reason={reason}", this);
        foreach (Action<int, string> listener in listeners.GetInvocationList())
        {
            try { listener(m_FormalResponseGeneration, reason); }
            catch (Exception error) { Debug.LogException(error, this); }
        }
    }

    private void OnDisable()
    {
        m_LastMotionProtocolRejection = null;
        ResetActionPlanning();
        CancelDialogueMotion("chat-disabled");
    }
}
