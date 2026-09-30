using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Recovery does not adopt a task. A new, validated role decision selects its
    // domain and still passes through the normal Actor and execution gates.
    private bool TryRepairUnavailableFastDecision(FastLoopTurnContext turn, string currentUser,
        string headId, int headRevision, string rejection)
    {
        if (!FastLoopTurnCurrent(turn) || turn.InvalidDecisionRepairRequested ||
            rejection == "http_failure" || rejection == "fast_loop_disabled" ||
            !(turn.Model is IFastLoopDecisionBackend backend) || !backend.SupportsFastLoopDecisions)
            return false;
        turn.InvalidDecisionRepairRequested = true;
        CancelActorSpeechPreparation("invalid-fast-decision");
        if (!FastLoopTurnCurrent(turn)) return true;
        var facts = BuildFastReplyRecoveryFacts(turn);
        facts["decisionRepair"] = new JObject {
            ["attempt"] = 1, ["rejection"] = rejection ?? "decision-unavailable",
            ["scope"] = "The prior response was invalid and has not authorized or published anything. " +
                "Make one fresh role choice for the complete current_user, using current facts. " +
                "Singing, a quotation, feedback or a question alone is not a spatial instruction. " +
                "Use reply for an appropriate response, recording_singing for an adopted recording goal, " +
                "or legacy only when a currently adopted goal actually needs the full planner. " +
                "A failed decision is not evidence of such a goal. Historical control evidence cannot authorize a new stop; " +
                "any control choice must cite the exact current input and match its actual task owner. " +
                "Refusal, clarification and choosing no new action remain available."
        };
        bool consumed = false;
        var trace = m_ResponseTrace?.Child("fast-decision-repair");
        trace?.Mark("fast_loop.decision_repair_requested", new JObject {
            ["generation"] = turn.Generation, ["rejection"] = rejection,
            ["attempt"] = 1, ["originalInputRetained"] = true, ["published"] = false });
        void Resolve(FastLoopDecision answer, string status)
        {
            if (consumed || !FastLoopTurnCurrent(turn)) return;
            consumed = true;
            bool valid = status == "ok" && answer != null &&
                FastLoopProtocol.Validate(answer, currentUser, out _);
            trace?.Mark("fast_loop.decision_repair_completed", new JObject {
                ["generation"] = turn.Generation, ["status"] = status,
                ["accepted"] = valid, ["route"] = valid ? answer.Route : null,
                ["scope"] = "A fresh model choice, not permission inferred from rejection or old dialogue." });
            if (!FastLoopTurnCurrent(turn)) return;
            if (!valid)
            {
                CompleteFastReplyRecoveryFailure(turn, answer?.Language ?? m_ActorTaskLanguage,
                    "decision-repair-unresolved:" + status);
                return;
            }
            ApplyFastLoopAnswer(turn, answer, currentUser, headId, headRevision, trace, status);
        }
        PrepareFastReplyVision(turn, facts, "decision-repair", trace, image => {
            if (!FastLoopTurnCurrent(turn) || consumed) return;
            try { backend.PostFastLoopDecision(facts.ToString(Formatting.None), currentUser, Resolve, trace, image); }
            catch (Exception error)
            { if (consumed) throw; Resolve(null, "decision-repair-exception:" + error.GetType().Name); }
        });
        return true;
    }
}
