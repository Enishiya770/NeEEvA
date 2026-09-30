using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private static string ActorFollowDependencyReceipt(string language) => language == "ja"
        ? "続きはついていく動きが終わった後だけれど、終える条件がまだないので実行していないわ。"
        : language == "en" ? " The later goal depends on following ending; without an end condition, I haven't started it."
        : "后面的目标要等跟随结束，但目前没有结束条件，所以还没有执行。";
    private static string ActorPlanClosureText(ActorPlanRuntime plan, string language, bool noFurtherStep)
    {
        string Text(string zh, string ja, string en) => language == "ja" ? ja : language == "en" ? en : zh;
        JObject lastSuccess = null; bool hadOngoing = false;
        if (plan != null)
            foreach (var token in plan.Results)
                if (token is JObject result && (bool?)result["succeeded"] == true)
                { if ((string)result["outcome"] == "ongoing") hadOngoing = true; else lastSuccess = result; }
        JObject lastResult = plan?.Results.Last as JObject;
        string failure = lastResult != null && (bool?)lastResult["succeeded"] == false
            ? ActionOutcomeFailureText((string)lastResult["phase"], (string)lastResult["reason"], language) : null;
        if (lastSuccess == null && hadOngoing)
            return noFurtherStep
                ? Text("跟随任务此前已确认启动，本轮没有提交其他动作。", "ついていく動きは先に開始を確認したわ。このターンではほかの動きは指示していないわ。", "Following was confirmed started earlier; no other action was submitted in this turn.")
                : failure != null
                    ? Text("跟随任务此前已确认启动。", "ついていく動きは先に開始を確認したわ。", "Following was confirmed started earlier. ") + failure
                    : Text("跟随任务此前已确认启动，但后续步骤未能确认，没有继续执行。", "ついていく動きは先に開始を確認したけれど、続きは確認できず実行していないわ。", "Following was confirmed started earlier, but I couldn't confirm the next step and haven't executed it.");
        if (lastSuccess == null)
            return failure ?? Text("这一步还没确认完成，剩余步骤没有继续执行。",
                "この手順は完了を確認できなかったので、続きは実行していないわ。",
                "I couldn't confirm this step, so I haven't executed the remaining steps.");
        // Past results survive a later planner/transport error. This deliberately
        // makes a historical claim, not a new claim about current device state.
        string completed = (string)lastSuccess["intent"] == "set-object-state" && (string)lastSuccess["requestedState"] == "off"
            ? Text("刚才关灯的步骤已经确认完成。", "さっき灯りを消す手順は完了を確認したわ。", "The earlier step to switch the lamp off was confirmed complete.")
            : (string)lastSuccess["intent"] == "set-object-state" && (string)lastSuccess["requestedState"] == "on"
                ? Text("刚才开灯的步骤已经确认完成。", "さっき灯りを点ける手順は完了を確認したわ。", "The earlier step to switch the lamp on was confirmed complete.")
                : Text("前面的步骤已经确认完成。", "前の手順は完了を確認したわ。", "The earlier step was confirmed complete.");
        return completed + (noFurtherStep ? "" : failure ?? Text("但后续步骤未能确认，没有继续执行。",
            "ただ、続きは確認できず、実行していないわ。", " However, I couldn't confirm the next step and haven't executed it."));
    }

    private void PublishActorPlanClosure(ActorPlanRuntime plan, FastLoopTurnContext turn, bool noFurtherStep, string reason)
    {
        if (!ActorPlanOwnerCurrent(plan, turn) || !ClaimActorPlanClosure(plan)) return;
        RevokeActorBodyHandoff(plan, plan.State == "completed" ? "finite-plan-completed" : reason);
        string language = ActorTaskLanguageFor(turn.Generation);
        m_ResponseTrace?.Mark("actor_plan.closed", new JObject { ["id"] = plan.Id,
            ["state"] = plan.State, ["reason"] = reason, ["resultCount"] = plan.Results.Count,
            ["remainingGoal"] = plan.RemainingGoal, ["newActionSubmitted"] = false });
        if (!ActorPlanOwnerCurrent(plan, turn)) return;
        PublishActorTaskReceipt(turn, ActorPlanClosureText(plan, language, noFurtherStep), language, "persistent-plan-closure", false);
    }

    private ActorPlanAcceptanceLease m_ActorPlanAcceptance;

    private sealed class ActorPlanAcceptanceLease
    {
        public ActorPlanRuntime Plan;
        public FastLoopTurnContext Turn;
        public int Round, PlanRevision, Revocation;
        public string ActionId, Text, Language;
        public LLM.SendData History, FallbackUser;
        public bool Revoked, HistoryCompleted;
    }

    // Called only after this step has both a bound action and a real executor
    // acceptance fact. The private decision alone cannot authorize an ack.
    private bool TryPublishActorPlanAcceptance(ActorPlanRuntime plan, FastLoopTurnContext turn,
        RoomTaskProtocol.Decision decision, string actionId)
    {
        if (!ActorPlanCurrent(plan, turn) || decision == null || decision.planId != plan.Id ||
            decision.planRevision != plan.Revision || plan.State != "executing" ||
            plan.PendingActionId != actionId || string.IsNullOrEmpty(actionId) ||
            ReferenceEquals(m_ActorPlanAcceptance?.Plan, plan) ||
            !HasActorPlanAcceptanceFact(decision, turn.Generation, actionId)) return false;

        string language = ActorTaskLanguageFor(turn.Generation);
        if (language != "zh" && language != "ja" && language != "en") language = "ja";
        bool observedPrerequisite = HasActorPlanObservationFact(decision, m_SceneActionFact);
        string text = observedPrerequisite
            ? language == "zh" ? "我已经确认了目标。" : language == "en"
                ? "I've confirmed the target." : "対象を確認できたわ。"
            : language == "zh" ? "我已经开始尝试这一步了。" :
            language == "en" ? "I've started attempting this step." : "この手順に取りかかったわ。";
        var previous = m_ActorPlanAcceptance;
        if (previous != null) RevokeActorPlanAcceptanceLease(previous, "replaced-by-new-plan-acceptance");
        if (!ActorPlanCurrent(plan, turn) || !ReferenceEquals(m_ActorPlanAcceptance, previous)) return false;
        m_SpeechRoundSequence = Math.Max(m_SpeechRoundSequence, m_CurrentSpeechRoundId);
        var lease = new ActorPlanAcceptanceLease {
            Plan = plan, Turn = turn, PlanRevision = plan.Revision,
            ActionId = actionId, Revocation = m_ActorPlanRevocation,
            // A dedicated chunk round makes cancellation precise: later result
            // speech in turn.Round must not be silenced with this acknowledgement.
            Round = ++m_SpeechRoundSequence, Text = text, Language = language
        };
        m_ActorPlanAcceptance = lease;
        m_ResponseTrace?.Mark("actor_plan.accepted_receipt", new JObject {
            ["id"] = plan.Id, ["step"] = lease.PlanRevision, ["actionId"] = actionId,
            ["generation"] = turn.Generation, ["speechRound"] = lease.Round,
            ["intent"] = decision.intent, ["language"] = language,
            ["receiptKind"] = observedPrerequisite ? "prerequisite-observed" : "attempt-accepted",
            ["scope"] = "An observed prerequisite or accepted attempt only; remaining steps are not claimed complete. Speech and subsequent planning/execution may proceed concurrently."
        });
        // Trace sinks are synchronous and may replace or cancel the owner.
        if (!ActorPlanAcceptanceCurrent(lease) || !ActorPlanCurrent(plan, turn) ||
            !HasActorPlanAcceptanceFact(decision, turn.Generation, actionId))
        { RevokeActorPlanAcceptanceLease(lease, "acceptance-owner-reentered"); return false; }

        if (turn.RecordHistory)
        {
            // Do not close the provider's accepted-user handoff here: a later
            // nonspatial step may still enter its existing full planner.
            if (!(turn.Model is IFastLoopHistoryBackend))
            {
                lease.FallbackUser = new LLM.SendData("user", turn.Prompt ?? "");
                turn.Model.m_DataList.Add(lease.FallbackUser);
            }
            lease.History = new LLM.SendData("assistant", ActorPlanAcceptanceEnvelope(text, language));
            turn.Model.m_DataList.Add(lease.History);
        }
        m_PendingChunks.Enqueue(new PendingSpeechChunk {
            RoundId = lease.Round, Text = text, LanguageCode = language
        });
        // No OnStreamComplete/CompleteFastLoopReply: keep both the speech
        // consumer and plan owner alive while the physical watcher proceeds.
        m_FirstChunkFlushed = true;
        return true;
    }

    private bool HasActorPlanAcceptanceFact(RoomTaskProtocol.Decision decision, int generation, string actionId)
    {
        if (m_RoomTaskGeneration != generation || m_RoomTaskActionId != actionId ||
            m_RoomTaskIntent != decision.intent || HasDefinitiveRoomActionFailure()) return false;
        if (decision.intent == "approach" || decision.intent == "follow")
        {
            var bridge = GetComponent<ArdyRoomDialogueBridge>();
            return RoomMotionOutputEnabled && bridge != null && bridge.IsConnected &&
                bridge.ActionId == actionId && bridge.ActiveTaskId == actionId &&
                bridge.TaskMode == decision.intent && ActorTaskActivePhase(decision.intent, bridge.Phase);
        }
        if (decision.intent == "explore")
            return m_SceneExploration.active && m_SceneExploration.goalId == actionId &&
                m_RoomTaskDispatch == "exploration-active";
        if (IsSceneVisualIntent(decision.intent))
        {
            var fact = m_SceneActionFact;
            return fact != null && fact.actionId == actionId && fact.intent == decision.intent &&
                (HasActorPlanObservationFact(decision, fact) || fact.phase == "submitted" || fact.phase == "arrival-observing" ||
                 fact.phase == "turning-body" || fact.phase == "looking" || fact.phase == "state-applied" ||
                 fact.phase == "state-applied-unconfirmed");
        }
        // Instantaneous inspect/release/stop results can use the existing final
        // receipt; a speculative early acknowledgement adds no useful overlap.
        return false;
    }

    private static bool HasActorPlanObservationFact(RoomTaskProtocol.Decision decision, SceneActionFact fact) =>
        decision != null && !string.IsNullOrWhiteSpace(decision.planRemaining) &&
        (decision.intent == "inspect-object" || decision.intent == "reobserve-object") &&
        fact != null && fact.intent == decision.intent && fact.phase == "observed" && fact.objectConfirmed &&
        !string.IsNullOrEmpty(fact.entityId) &&
        (!string.IsNullOrEmpty(fact.confirmedCaptureId) || !string.IsNullOrEmpty(fact.resultCaptureId));

    private bool ActorPlanAcceptanceCurrent(ActorPlanAcceptanceLease lease)
    {
        var turn = lease?.Turn;
        return lease != null && !lease.Revoked && ReferenceEquals(m_ActorPlanAcceptance, lease) &&
            ReferenceEquals(m_ActorPlan, lease.Plan) && ReferenceEquals(m_ActorPlanTurn, turn) &&
            lease.Plan.State != "cancelled" && lease.Revocation == m_RoomActorProposalRevocation &&
            turn != null && turn.Generation == m_FormalResponseGeneration && turn.Epoch == m_WorkEpoch &&
            turn.OutputRevision == m_FastLoop.OutputRevision && turn.Round == m_CurrentSpeechRoundId &&
            ReferenceEquals(turn.Model, m_ChatSettings?.m_ChatModel) && isActiveAndEnabled &&
            !m_SceneVisionApplicationPaused && !m_UserSpeechActiveForAutonomy &&
            !m_SilencedSpeechRoundIds.Contains(turn.Round) && !m_SilencedSpeechRoundIds.Contains(lease.Round);
    }

    private bool ValidateActorPlanAcceptanceSpeech(int roundId)
    {
        var lease = m_ActorPlanAcceptance;
        if (lease == null || lease.Round != roundId) return true;
        // It remains a true acceptance fact after completion and after the
        // formal turn is cleared. No final-result or next-step speech is gated.
        if (ActorPlanAcceptanceCurrent(lease)) return true;
        RevokeActorPlanAcceptanceLease(lease, "acceptance-owner-invalidated");
        return false;
    }

    private void RevokeActorPlanAcceptance(ActorPlanRuntime plan, string reason)
    {
        var lease = m_ActorPlanAcceptance;
        if (lease != null && ReferenceEquals(lease.Plan, plan)) RevokeActorPlanAcceptanceLease(lease, reason);
    }

    private void RevokeActorPlanAcceptanceLease(ActorPlanAcceptanceLease lease, string reason)
    {
        if (lease == null || lease.Revoked) return;
        lease.Revoked = true;
        // Remove only an unplayed acknowledgement's own history object, never
        // another turn or a merged final result.
        if (!lease.HistoryCompleted && lease.History != null &&
            !HasRoundSpeechStarted(lease.Turn.Generation, lease.Round))
            lease.Turn.Model.m_DataList.Remove(lease.History);
        DiscardPendingSpeechForRound(lease.Round, "actor-plan-acceptance:" + reason);
    }

    // CompleteFastLoopReply calls this inside its recordHistory block. Normal
    // callers retain their old history path; an early ack is extended once.
    private bool TryCompleteActorPlanAcceptanceHistory(LLM model, int generation, string envelope)
    {
        var lease = m_ActorPlanAcceptance;
        if (lease == null || lease.Turn.Generation != generation || !ReferenceEquals(lease.Turn.Model, model)) return false;
        if (lease.History == null || !model.m_DataList.Contains(lease.History))
        {
            // Cancellation may have removed an unplayed ack while a same-turn
            // correction is still allowed. Preserve its one accepted fallback
            // user; do not append that user a second time with the correction.
            if (lease.FallbackUser == null || !model.m_DataList.Contains(lease.FallbackUser)) return false;
            lease.History = new LLM.SendData("assistant", envelope);
            model.m_DataList.Add(lease.History);
            lease.HistoryCompleted = true;
            model.CheckHistory();
            return true;
        }
        if (lease.HistoryCompleted) return true;
        if (model is IFastLoopHistoryBackend history) history.CompleteFastLoopHistory();
        lease.HistoryCompleted = true;
        if (!string.IsNullOrEmpty(envelope) && envelope != "<silent/>")
            lease.History.content += "\n" + envelope;
        model.CheckHistory();
        return true;
    }

    private static string ActorPlanAcceptanceEnvelope(string text, string language) =>
        "<lang code=\"" + language + "\"/><speech mode=\"independent\"/><say>" +
        System.Security.SecurityElement.Escape(text) + "</say>";
}
