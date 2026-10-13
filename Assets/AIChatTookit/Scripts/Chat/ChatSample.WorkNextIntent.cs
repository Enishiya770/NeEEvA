using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private bool m_WorkAutonomousHandoff, m_WorkReplyOnly;
    private string m_WorkNextStepId;
    private JObject m_SceneAutonomyAttention;

    private bool AcceptWorkAutonomousHandoff(AutonomyIntentDecision decision, JObject sequence)
    {
        if (decision.next_intent_source != "autonomous" || !decision.proceed ||
            string.IsNullOrWhiteSpace(decision.intent) || !string.IsNullOrEmpty(decision.next_step_id) ||
            !new[] { "closed", "none" }.Contains(decision.work_status) ||
            !string.IsNullOrEmpty(m_PendingSelfInspection) || HasWorkToolInFlight() ||
            SpatialGoalPreventsWorkClosure() || IsSingingGoalAwaitingConfirmation() ||
            IsSingingGoalAwaitingExecution() && !HasAutonomousSingingGoal() ||
            sequence?["steps"] is JArray steps && steps.Count > 0 && !WorkNextIntentProtocol.AllStepsCompleted(sequence)) return false;
        m_WorkStatus = "closed"; m_WorkReviewOpen = false; m_WorkAutonomousHandoff = true;
        LogWorkNextIntent(decision, "new-autonomous-intention", sequence);
        return true;
    }

    private void SelectWorkContinuationScope(AutonomyIntentDecision decision, JObject sequence)
    {
        m_WorkNextStepId = decision.next_step_id;
        // A reply remains possible when the executor has no pending step. It
        // must not reopen the full action planner or replay a completed tool.
        m_WorkReplyOnly = sequence != null && (!WorkNextIntentProtocol.HasReadyStep(sequence, m_WorkNextStepId) ||
            decision.next_intent_source == "current-work" && string.IsNullOrEmpty(m_WorkNextStepId));
        if (decision.next_intent_source == "autonomous" || decision.next_intent_source == "none")
            m_WorkReplyOnly = true; // Inconsistent source/status cannot acquire old-task action authority.
        LogWorkNextIntent(decision, m_WorkReplyOnly ? "remaining-answer-or-diagnosis" : "current-work", sequence);
    }

    private void LogWorkNextIntent(AutonomyIntentDecision decision, string disposition, JObject sequence)
    {
        UnityEngine.Debug.Log("[Work/NextIntent] " + new JObject {
            ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["workStatus"] = m_WorkStatus, ["source"] = decision.next_intent_source,
            ["nextStepId"] = decision.next_step_id, ["planningFocus"] = decision.planning_focus,
            ["evidence"] = decision.work_evidence, ["intent"] = decision.intent,
            ["disposition"] = disposition, ["taskProgress"] = WorkNextIntentProtocol.Progress(sequence),
            ["newActionSubmitted"] = false
        }.ToString(Formatting.None), this);
    }

    private void BindAutonomyAttention(AutonomyIntentDecision decision)
    {
        m_SceneAutonomyAttention = decision == null ? null : new JObject {
            ["source"] = "current-private-intent-review", ["kind"] = decision.planning_focus ?? "unknown",
            ["intention"] = decision.intent ?? "", ["workEpoch"] = m_WorkEpoch,
            ["userTurn"] = m_ActionConstraints.CurrentUserTurn, ["actionAuthorization"] = false
        };
    }

    private JObject ReadAutonomyAttention() => m_SceneAutonomyAttention != null &&
        (int?)m_SceneAutonomyAttention["workEpoch"] == m_WorkEpoch &&
        (int?)m_SceneAutonomyAttention["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
        (string)m_SceneAutonomyAttention["intention"] == m_SceneAutonomyIntention
            ? (JObject)m_SceneAutonomyAttention.DeepClone() : null;
}
