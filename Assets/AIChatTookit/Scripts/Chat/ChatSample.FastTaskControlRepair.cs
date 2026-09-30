using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // The denied draft is never spoken. A new role choice still goes through
    // the Actor compiler and actual execution/result gates, without assuming
    // that the original user's feedback was a new spatial instruction.
    private bool TryRepairDeclinedFastControl(FastLoopTurnContext turn, string language,
        string currentUser, string headId, int headRevision)
    {
        if (!FastLoopTurnCurrent(turn) || !m_EnableActorIntentCompiler ||
            !(turn.Model is IFastLoopDecisionBackend backend) || !backend.SupportsFastLoopDecisions ||
            !(turn.Model is IActorIntentBackend actor) || !actor.SupportsActorIntents)
            return false;
        if (turn.ControlRepairRequested)
        {
            CompleteFastReplyRecoveryFailure(turn, language, "task-control-repair-exhausted");
            return true;
        }
        turn.ControlRepairRequested = true;
        CancelActorSpeechPreparation("task-control-not-accepted");
        if (!FastLoopTurnCurrent(turn)) return true;
        var facts = BuildFastReplyRecoveryFacts(turn);
        facts["taskControlRepair"] = new JObject {
            ["attempt"] = 1, ["result"] = turn.ControlResult?.DeepClone(),
            ["scope"] = "The previous stop proposal was not accepted; its draft was never published. " +
                "Reconsider the complete current user input and actual current task facts. " +
                "Recent completed assistant speech remains heard dialogue, including clarification questions. " +
                "Interpret short confirmations against that question and the still-unmet goal; denying a stop does not cancel either. " +
                "Cancellation of a request or planning owner only revokes that attempt's authority; it is not evidence that the user cancelled the semantic goal. " +
                "If this is feedback, explanation or praise, answer it naturally without inventing a new task. " +
                "Preserve any genuinely new or mixed goal using the ordinary route/Actor capabilities. " +
                "Do not repeat an unexecuted stop acknowledgement. This repair cannot authorize another body stop; task_control=none."
        };
        bool consumed = false;
        var trace = m_ResponseTrace?.Child("task-control-repair");
        trace?.Mark("fast_loop.control_repair_requested", new JObject {
            ["generation"] = turn.Generation, ["attempt"] = 1,
            ["originalInputRetained"] = true, ["published"] = false });
        if (!FastLoopTurnCurrent(turn)) return true;
        void Resolve(FastLoopDecision answer, string status)
        {
            if (consumed || !FastLoopTurnCurrent(turn)) return;
            consumed = true;
            bool valid = status == "ok" && answer != null &&
                FastLoopProtocol.Validate(answer, currentUser, out _) && answer.TaskControl == "none";
            trace?.Mark("fast_loop.control_repair_completed", new JObject {
                ["status"] = status, ["route"] = answer?.Route, ["accepted"] = valid,
                ["scope"] = "Repaired role choice only; ordinary Actor and execution checks still required." });
            if (!FastLoopTurnCurrent(turn)) return;
            if (!valid)
            {
                CompleteFastReplyRecoveryFailure(turn, language, "task-control-repair-unresolved");
                return;
            }
            // Keep the denial as a fact; never clear it to reauthorize a stop.
            ApplyFastLoopAnswer(turn, answer, currentUser, headId, headRevision, trace);
        }
        PrepareFastReplyVision(turn, facts, "task-control-repair", trace, image => {
            if (!FastLoopTurnCurrent(turn) || consumed) return;
            try { backend.PostFastLoopDecision(facts.ToString(Formatting.None), currentUser, Resolve, trace, image); }
            catch (Exception error)
            { if (consumed) throw; Resolve(null, "control-repair-exception:" + error.GetType().Name); }
        });
        return true;
    }
}
