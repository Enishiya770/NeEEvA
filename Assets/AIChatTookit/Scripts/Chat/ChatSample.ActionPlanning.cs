using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using UnityEngine;

public partial class ChatSample
{
    private readonly ArdyActionConstraintLedger m_ActionConstraints = new ArdyActionConstraintLedger();
    private int m_ActionResponseUserTurn = -1;
    private int? m_ActionCallbackUserTurn;
    private int m_ActionRequestEpoch;
    private string m_LastActionPlanError = "";

    [Serializable] private sealed class ActionPlanningFacts
    {
        public int userTurn;
        public string userText, purpose, lastPlanError;
        public ArdyActionConstraintLedger.Entry[] constraints;
        public string meaning = "User requirements with checked source quotes; historical intent, not measured pose. Quote matching does not prove semantic entailment.";
    }

    private void RecordAcceptedMotionUserTurn(string text)
    {
        // Inspector changes must reset the previous mode before accepting this
        // new user's text, not while its response context is being assembled.
        if (m_ConfiguredSemanticMotionPlanning != m_UseSemanticMotionPlanning)
            ConfigureMotionOutput(m_MotionOutputEnabled, m_GeneratedMotionEnabled);
        m_ActionConstraints.RecordUserTurn(text);
    }

    private void BeginMotionPlanningResponse()
    {
        m_ActionResponseUserTurn = m_ActionCallbackUserTurn ?? m_ActionConstraints.CurrentUserTurn;
    }

    // Non-streaming responses start their formal generation when the callback arrives.
    // Capture the source now so an old callback cannot acquire a newer user's authority.
    private Action<List<SpeechText>, string> CaptureMotionResponseCallback()
    {
        int source = m_ActionConstraints.CurrentUserTurn;
        int epoch = ++m_ActionRequestEpoch;
        int generation = m_FormalResponseGeneration;
        ObservationReceipt observations = CaptureObservationReceipt();
        return (speech, executable) =>
        {
            // Discard obsolete callbacks before they can start a generation, clear
            // deduplication, enqueue speech, or write feedback into the newer turn.
            if (source != m_ActionConstraints.CurrentUserTurn || epoch != m_ActionRequestEpoch ||
                generation != m_FormalResponseGeneration || (observations != null && observations.Epoch != m_WorkEpoch)) return;
            AcknowledgeObservations(observations, generation, executable);
            m_FormalResponseInFlight = false;
            var previous = m_ActionCallbackUserTurn;
            m_ActionCallbackUserTurn = source;
            try { CallBackWithSpeech(speech, executable); }
            finally { m_ActionCallbackUserTurn = previous; }
        };
    }

    private string BuildActionPlanningFacts()
    {
        string text = m_ActionConstraints.CurrentUserText;
        if (text.Length > 2048) text = text.Substring(0, 2048);
        return "\n[动作意图与用户约束；历史要求，不是已达到姿态]\n" + JsonUtility.ToJson(new ActionPlanningFacts {
            userTurn = m_ActionConstraints.CurrentUserTurn, userText = text,
            purpose = m_ActionConstraints.Purpose, constraints = m_ActionConstraints.Entries,
            lastPlanError = m_LastActionPlanError });
    }

    private bool TryResolveActionPlan(ref DialogueMotionIntent intent)
    {
        int source = m_ActionResponseUserTurn >= 0 ? m_ActionResponseUserTurn : m_ActionConstraints.CurrentUserTurn;
        if (source != m_ActionConstraints.CurrentUserTurn) return false;
        try
        {
            // Room destinations and arm-pose constraints are independent channels.
            // Neither a walk nor its stop command may erase or inherit wrist/arm locks.
            if (intent.IsRoomMotion) return true;
            if (intent.Name == "none")
            {
                m_ActionConstraints.ClearGoalOnStop(source);
                m_LastActionPlanError = "";
                return true;
            }
            if (intent.Name != "plan")
            {
                if (m_ActionConstraints.HasConstraints)
                    throw new ArgumentException("当前有明确用户约束，请用plan继承或凭当前用户依据开启新目标，不能通过旧动作通道跳过约束。");
                m_LastActionPlanError = "";
                return true;
            }
            var prepared = m_ActionConstraints.Prepare(intent.ActionPlan, source);
            var compiled = ArdyActionPlanCompiler.Compile(prepared.effective);
            // This records the user's request. A later execution rejection remains
            // separate Controller feedback and must never erase a hard requirement.
            m_ActionConstraints.Commit(prepared);
            intent = new DialogueMotionIntent(compiled.name, compiled.description,
                intent.ResponseGeneration, intent.Sequence, compiled.controlPlan, null,
                intent.Goal, intent.ReplayReference, intent.ActionId, intent.ParentActionId, intent.RepairAttempt);
            m_LastActionPlanError = "";
            return true;
        }
        catch (ArgumentException error)
        {
            m_LastActionPlanError = error.Message;
            Debug.LogWarning("[Dialogue/MotionPlan] " + error.Message, this);
            return false;
        }
        catch (InvalidOperationException error)
        {
            m_LastActionPlanError = error.Message;
            Debug.LogWarning("[Dialogue/MotionPlan] " + error.Message, this);
            return false;
        }
    }

    private void ResetActionPlanning()
    {
        m_ActionConstraints.Reset();
        m_LastActionPlanError = "";
        ++m_ActionRequestEpoch;
        // Keep the old response's source snapshot. Rebinding must not let an
        // in-flight stream acquire the new ledger's authority via a fallback.
    }
}
