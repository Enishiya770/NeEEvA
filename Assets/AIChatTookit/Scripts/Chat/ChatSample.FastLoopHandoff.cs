using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // One accepted turn owns both the quick decision and a possible planner
    // continuation. Never reconstruct this from a newer user input or from the
    // last public sentence after an asynchronous callback.
    private sealed class FastLoopTurnContext
    {
        public readonly LLM Model;
        public readonly string Prompt, TransientContext;
        public string Image;
        public readonly int Generation, Epoch, Round;
        public readonly long OutputRevision;
        public readonly bool RecordHistory;
        public bool HandedOff;
        public bool PlannerEntryClaimed;
        public bool DialogueRepairRequested;
        public bool InvalidDecisionRepairRequested;
        public FastTaskControlSnapshot BodyControl;
        public JObject ControlResult;
        public bool TaskControlReviewed;
        public bool TaskControlReviewPending;
        public bool ControlRepairRequested;
        public JObject ActorProposal;

        public FastLoopTurnContext(LLM model, string prompt, string image, string context,
            int generation, int epoch, int round, long outputRevision, bool recordHistory)
        {
            Model = model; Prompt = prompt; Image = image; TransientContext = context;
            Generation = generation; Epoch = epoch; Round = round;
            OutputRevision = outputRevision; RecordHistory = recordHistory;
        }
    }

    private bool FastLoopTurnCurrent(FastLoopTurnContext turn) => turn != null &&
        ReferenceEquals(m_FastLoopTurn, turn) && turn.Generation == m_FormalResponseGeneration &&
        turn.Epoch == m_WorkEpoch && turn.Round == m_CurrentSpeechRoundId &&
        turn.OutputRevision == m_FastLoop.OutputRevision &&
        ReferenceEquals(turn.Model, m_ChatSettings?.m_ChatModel) && isActiveAndEnabled &&
        !m_SceneVisionApplicationPaused && !m_UserSpeechActiveForAutonomy &&
        !m_SilencedSpeechRoundIds.Contains(turn.Round);

    private JObject ReadFastLoopPlannerProposal(int generation)
    {
        var turn = m_FastLoopTurn;
        return FastLoopTurnCurrent(turn) && turn.Generation == generation && turn.HandedOff
            ? turn.ActorProposal?.DeepClone() as JObject : null;
    }

    private bool TryClaimFastLoopPlannerEntry(int generation, bool plannerContinuation)
    {
        var turn = m_FastLoopTurn;
        if (!FastLoopTurnCurrent(turn) || generation != turn.Generation || !turn.HandedOff)
            return true;
        if ((string)turn.ActorProposal?["purpose"] == "verify") return false;
        if (plannerContinuation) return true;
        if (turn.PlannerEntryClaimed) return false;
        turn.PlannerEntryClaimed = true;
        return true;
    }

    private void HandoffFastLoopToPlanner(LLM model, int generation, long outputRevision,
        ActorDraft draft, ActorIntentDecision decision, string reason, bool recordingSinging = false)
    {
        var turn = m_FastLoopTurn;
        if (!FastLoopTurnCurrent(turn) || !ReferenceEquals(model, turn.Model) ||
            generation != turn.Generation || outputRevision != turn.OutputRevision || turn.HandedOff ||
            m_FormalTransportDispatchGeneration == generation) return;
        if (decision?.RequiresFullReview == true)
        {
            // A compiler conflict has no physical authority, even if a caller
            // accidentally tries to route it past the semantic review stage.
            CompleteActorCorrection(model, turn.Prompt, m_ActorTaskLanguage ?? "ja", generation,
                turn.RecordHistory, "actor-semantic-review-required");
            return;
        }
        // Claim before disposal: a provider's cancellation callback may reenter.
        turn.HandedOff = true;
        CancelClosedRoomPreparation("owned-goal-planner-selected");
        if (!FastLoopTurnCurrent(turn)) return;
        turn.ActorProposal = draft == null ? null : new JObject {
            ["source"] = "unpublished-current-role-draft", ["draftId"] = draft.Id,
            ["draftRevision"] = draft.Revision, ["text"] = draft.Text ?? "",
            ["domain"] = decision?.Domain, ["evidence"] = decision?.Evidence,
            ["task"] = decision?.Task, ["target"] = decision?.Target,
            ["goal"] = decision?.Goal, ["coverage"] = decision?.Coverage,
            ["currentFactPrerequisite"] = decision?.CurrentFactPrerequisite == true,
            ["reviewedCoverage"] = decision?.ReviewedCoverage,
            ["motionScope"] = decision?.MotionScope, ["degrees"] = decision?.Degrees,
            ["deviceState"] = decision?.DeviceState, ["targetEntityId"] = decision?.TargetEntityId,
            ["deviceTargetReferenceBound"] = decision?.DeviceTargetIsProgramBound == true,
            ["originalUserText"] = FastLoopCurrentUserText(turn.Prompt),
            ["classificationUncertain"] = decision?.RequiresFullReview == true, ["classificationFailure"] = decision?.ReviewReason,
            ["purpose"] = decision?.Kind == "claim" || reason == "unsupported-wait-trigger" ? "verify" : "plan",
            ["reviewKind"] = reason == "unsupported-wait-trigger" || decision?.OriginalKind == "wait" ? "unsupported-condition" : "completion",
            ["generation"] = generation, ["epoch"] = turn.Epoch,
            ["outputRevision"] = outputRevision, ["executed"] = false, ["published"] = false,
            ["scope"] = "The role's current proposal, not a user instruction or execution result. Resolve it using the actual current user, capabilities, material IDs and observed task facts. Preserve the role's choice; a planner may execute, clarify, retract or decline. Past-tense claims still need real results. Never fabricate user evidence, a target, source confirmation or playback permission."
        };
        CancelActorSpeechPreparation("planner-handoff");
        if (!FastLoopTurnCurrent(turn)) return;
        // Preserve the first-reply fence and exact user history lease, but release
        // the quick dispatch fence so the original formal entry can run once.
        m_FastLoopDispatchGeneration = -1;
        m_FormalResponseInFlight = false;
        ++m_ActorIntentRequestSerial;
        m_ActorIntentBackend = null;
        if ((string)turn.ActorProposal?["purpose"] == "verify")
        {
            ReviewActorCompletionWithoutExecution(turn, draft);
            return;
        }
        // The typed recording stage owns its independent source/range review.
        // A spatial plan with no executable room step would block that review.
        if (draft != null && !recordingSinging) BeginActorPlan(turn, draft, decision);
        else if (!recordingSinging && m_EnableActorIntentCompiler) BeginUserGoalPlan(turn);
        string context = (turn.TransientContext ?? "") + "\n[持续任务与当前能力事实]\n" + BuildFastLoopContext();
        if (turn.ActorProposal != null)
            context += "\n[本轮角色提案；尚未公开或执行，需实际规划与事实验证]\n" +
                turn.ActorProposal.ToString(Formatting.None);
        m_ResponseTrace?.Mark("fast_loop.planner_handoff", new JObject {
            ["generation"] = generation, ["epoch"] = turn.Epoch, ["reason"] = reason,
            ["draftId"] = draft?.Id, ["domain"] = decision?.Domain,
            ["captureId"] = GetFormalSceneObservation(generation)?.CaptureId ?? "",
            ["originalInputRetained"] = true, ["publicDraftPublished"] = false,
            ["taskControlResult"] = turn.ControlResult?.DeepClone(),
            ["scope"] = "Same-turn ownership of the remaining goal. This handoff is not execution or completion of that goal; any separate task stop is described only by taskControlResult."
        });
        DispatchFormalStream(turn.Prompt, turn.Image, generation, turn.RecordHistory, context,
            roomTaskPrepared: recordingSinging, skipRoomPlanner: recordingSinging, recordingSinging: recordingSinging);
    }

    private void ReviewActorCompletionWithoutExecution(FastLoopTurnContext turn, ActorDraft draft)
    {
        // No free-text reviewer can certify the complete claim when its output
        // has no typed action/entity/result selector. Do not let an image or an
        // unrelated old result become proof of a newly asserted device state.
        if (!FastLoopTurnCurrent(turn)) return;
        string language = m_ActorTaskLanguageGeneration == turn.Generation ? m_ActorTaskLanguage : "ja";
        bool condition = (string)turn.ActorProposal?["reviewKind"] == "unsupported-condition";
        string text = condition
            ? language == "zh" ? "这个等待条件还没有可靠的触发方式，我没有安排或开始这项动作。" :
              language == "en" ? "That condition has no reliable trigger yet; I haven't scheduled or started the action." :
              "その条件を確実に検知する方法がまだないので、動作は予約も開始もしていないわ。"
            : language == "zh" ? "我还没有核实这句话中全部动作的结果，不能把它当作已经完成。" :
              language == "en" ? "I haven't verified all the action results in that statement, so I can't say it is complete." :
              "その発言に含まれる動作の結果をすべて確認できていないので、完了したとは言えないわ。";
        m_ResponseTrace?.Mark("actor_intent.verify_only", new JObject {
            ["draftId"] = draft.Id, ["generation"] = turn.Generation,
            ["executionAllowed"] = false, ["freeTextVerificationAllowed"] = false,
            ["reason"] = condition ? "unsupported-condition" : "typed-completion-evidence-unavailable" });
        CompleteFastLoopReply(turn.Model, turn.Prompt, text, language, turn.Generation, turn.RecordHistory);
    }
}
