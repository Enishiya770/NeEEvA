using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("Judge complete unpublished fast-loop speech before publishing. Requires the experimental fast task loop.")]
    private bool m_EnableActorIntentCompiler = true;
    private IActorIntentBackend m_ActorIntentBackend;
    private int m_ActorIntentRequestSerial;
    private int m_ActorInputGeneration = -1;
    private int m_ActorConditionObservedGeneration = -1;
    private int m_ActorHistoryGeneration = -1;
    private long m_ActorInputSequence;
    private string m_ActorWaitingLanguage = "ja";
    private readonly ActorWaitingTaskRuntime m_ActorWaiting = new ActorWaitingTaskRuntime();
    private readonly Dictionary<int, ActorWaitSpeechLease> m_ActorWaitSpeech = new Dictionary<int, ActorWaitSpeechLease>();

    private sealed class ActorWaitSpeechLease
    {
        public string id;
        public int revision;
        public long outputRevision;
    }

    private void ObserveActorInputGeneration(int generation)
    {
        if (m_ActorInputGeneration == generation) return;
        m_ActorInputGeneration = generation;
        ++m_ActorInputSequence;
    }

    private void BeginActorAwareHistory(LLM model, string prompt, string imageUrl, int generation, bool recordHistory)
    {
        if (m_ActorHistoryGeneration == generation) return;
        m_ActorHistoryGeneration = generation;
        (model as IFastLoopHistoryBackend)?.BeginFastLoopHistory(prompt, imageUrl, recordHistory);
        if (recordHistory && model is IFastLoopHistoryBackend)
        {
            m_CurrentInputHistoryModel = model;
            m_CurrentInputHistoryUser = model.m_DataList.FindLast(x => x?.role == "user");
        }
    }

    private JObject BuildActorWaitingFacts()
    {
        var task = m_ActorWaiting.Pending;
        return task == null ? null : new JObject {
            ["id"] = task.Id, ["revision"] = task.Revision, ["state"] = task.State,
            ["action"] = task.Action, ["condition"] = task.Condition, ["progress"] = task.Progress,
            ["draft"] = task.DraftText, ["reason"] = task.Reason,
            ["armedAfterSequence"] = task.ArmedAfterSequence, ["lastSequence"] = task.LastSequence
        };
    }

    private string BuildActorIntentContext()
    {
        // Extract references without exposing the historical failure/action menu.
        // Completion truth is still checked separately against the real executor.
        var task = m_FastLoop.HeadTask;
        var facts = new JObject {
            ["waitingTask"] = BuildActorWaitingFacts(),
            ["currentPosture"] = task?.Active == true ? new JObject {
                ["candidate"] = task.Candidate, ["state"] = task.State,
                ["physicallyHeld"] = IsFastHeadPhysicallyHeld(task.Id, task.Revision) } : null,
            ["inputSequence"] = m_ActorInputSequence,
            ["spatialObjectMemory"] = BuildSpatialObjectMemory(),
            ["previousPlanReferent"] = PreviousActorPlanFacts(),
            ["motionCapabilities"] = new JObject { ["headPresetYawDegrees"] = 18, ["headPresetPitchDegrees"] = 12,
                ["preciseAngleRoute"] = "planner", ["bodyOrObjectGoalRoute"] = "planner",
                ["scope"] = "Only an explicitly chosen simple preset can use the direct head controller; these limits do not rewrite the character goal." },
            ["recordingReferent"] = m_SingingGoal == null ? null : new JObject {
                ["intent"] = m_SingingGoal.intent, ["proposedReferences"] = FastLoopPrompt.ObservationText(m_SingingGoal.refs, 180),
                ["expected"] = FastLoopPrompt.ObservationText(m_SingingGoal.expected, 180),
                ["scope"] = "Existing recording-goal referent only, not adopted intent in this draft, source/range approval or a playback receipt."
            },
            ["acceptedSingingSession"] = BuildActorSingingSessionFacts(),
            ["scope"] = "只用当前草稿与当前用户解析完整行为和指代。当前姿势不代表草稿选择该姿势；不从执行失败推导头部动作。"
        };
        return facts.ToString(Formatting.None);
    }

    private bool ActorRequestCurrent(LLM model, int generation, int epoch, long outputRevision, int serial) =>
        serial == m_ActorIntentRequestSerial && generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
        outputRevision == m_FastLoop.OutputRevision && isActiveAndEnabled && !m_SceneVisionApplicationPaused &&
        !m_UserSpeechActiveForAutonomy && m_EnableFastTaskLoop && m_EnableActorIntentCompiler &&
        ReferenceEquals(model, m_ChatSettings?.m_ChatModel);

    private void CancelActorIntentRequest()
    {
        ++m_ActorIntentRequestSerial;
        m_ActorIntentBackend?.CancelActorIntent();
        m_ActorIntentBackend = null;
        CancelActorSpeechPreparation("actor-intent-cancelled");
    }

    private void CompileActorDraft(LLM model, string prompt, string text, string language, int generation,
        bool recordHistory, long outputRevision)
    {
        CancelClosedRoomPreparation("role-draft-selected");
        CancelActorIntentRequest();
        if (!(model is IActorIntentBackend backend) || !backend.SupportsActorIntents)
        {
            CompleteActorInterpretationFailure(model, prompt, language, generation, recordHistory, "actor-judge-unavailable");
            return;
        }
        var draft = new ActorDraft { Id = Guid.NewGuid().ToString("N"), Revision = 1, Text = text ?? "" };
        m_ActorIntentBackend = backend;
        int serial = m_ActorIntentRequestSerial, epoch = m_WorkEpoch;
        var snapshot = m_FastLoop.HeadTask;
        var userTaskBinding = CaptureActorUserTaskBinding(generation);
        string legacyOwner = m_PersistentSceneAttention?.active == true ? m_PersistentSceneAttention.actionId : null;
        bool consumed = false;
        float started = Time.realtimeSinceStartup;
        var trace = m_ResponseTrace?.Child("actor-intent");
        // Preparation may synchronously reenter through a provider callback. The
        // old draft must retain its original request fence, never inherit the
        // serial/backend of a cancellation or newer draft created during it.
        BeginActorSpeechPreparation(draft, language, generation, outputRevision);
        if (!ActorRequestCurrent(model, generation, epoch, outputRevision, serial) ||
            !ReferenceEquals(m_ActorIntentBackend, backend) || !backend.SupportsActorIntents) return;
        trace?.Mark("actor_intent.dispatch", new JObject { ["draftId"] = draft.Id, ["revision"] = draft.Revision });
        string currentUser = FastLoopCurrentUserText(prompt);
        string intentContext = BuildActorIntentContext();
        var intentMemory = JObject.Parse(intentContext)["spatialObjectMemory"] as JObject;
        bool Current() => ActorRequestCurrent(model, generation, epoch, outputRevision, serial) &&
            ReferenceEquals(m_ActorIntentBackend, backend) && backend.SupportsActorIntents;
        void ApplyDecision(ActorIntentDecision decision, string status)
        {
            if (!Current()) return;
            bool valid = ActorIntentProtocol.Validate(decision, draft.Text, out string reason);
            if (valid && decision.DeviceState != "none")
            {
                ActorIntentProtocol.BindDeviceTarget(decision, intentMemory);
                if (decision.DeviceTargetIsProgramBound && !KnownSceneObjectBinding(decision.TargetEntityId, out _))
                    decision.DeviceTargetIsProgramBound = false;
            }
            Debug.Log($"[ActorIntent/Decision] draft={draft.Id} kind={decision?.Kind} action={decision?.Action} status={status} seconds={Time.realtimeSinceStartup - started:F3}", this);
            trace?.Mark("actor_intent.decision", new JObject { ["draftId"] = draft.Id, ["kind"] = decision?.Kind,
                ["domain"] = decision?.Domain, ["action"] = decision?.Action,
                ["task"] = decision?.Task, ["target"] = decision?.Target,
                ["coverage"] = decision?.Coverage, ["goal"] = decision?.Goal,
                ["currentFactPrerequisite"] = decision?.CurrentFactPrerequisite == true,
                ["reviewedCoverage"] = decision?.ReviewedCoverage,
                ["motionScope"] = decision?.MotionScope, ["degrees"] = decision?.Degrees,
                ["deviceState"] = decision?.DeviceState, ["targetEntityId"] = decision?.TargetEntityId,
                ["deviceTargetReferenceBound"] = decision?.DeviceTargetIsProgramBound == true,
                ["requiresFullReview"] = decision?.RequiresFullReview == true, ["reviewReason"] = decision?.ReviewReason,
                ["status"] = status, ["seconds"] = Time.realtimeSinceStartup - started,
                ["evidence"] = valid ? ActorEvidenceExcerpt(decision.Evidence) : "",
                ["evidenceCharacters"] = valid ? decision.Evidence.Length : 0 });
            // Trace observers may synchronously cancel or start a newer draft.
            if (!Current()) return;
            if (!valid)
            { CompleteActorInterpretationFailure(model, prompt, language, generation, recordHistory, reason ?? status); return; }
            RememberActorTaskLanguage(generation, language);
            if (decision.Kind == "none")
            { CompleteFastLoopReply(model, prompt, draft.Text, language, generation, recordHistory); return; }
            if (decision.Domain == "head" && !ActorIntentProtocol.CanUseHeadPreset(decision))
            {
                HandoffFastLoopToPlanner(model, generation, outputRevision, draft, decision, "actor-head-capability-not-covered");
                return;
            }
            if (decision.Domain == "head" && decision.Kind == "act" && !string.IsNullOrEmpty(decision.Goal) &&
                !string.Equals((decision.Evidence ?? "").Trim(), draft.Text.Trim(), StringComparison.Ordinal))
            {
                HandoffFastLoopToPlanner(model, generation, outputRevision, draft, decision, "actor-complete-goal-review");
                return;
            }
            if (decision.Kind == "wait")
            {
                // The only native counter is exactly three. Classification alone
                // cannot invent that finite parameter when neither current source
                // contains it (for example, a music-ended condition).
                if (!ActorCountThreeHasLiteralAnchor(draft.Text) && !ActorCountThreeHasLiteralAnchor(currentUser))
                {
                    HandoffFastLoopToPlanner(model, generation, outputRevision, draft, decision, "unsupported-wait-trigger");
                    return;
                }
                if (!m_ActorWaiting.Register(decision, draft, m_ActorInputSequence, out var waiting, out reason) || waiting.State != "waiting")
                { CompleteActorCorrection(model, prompt, language, generation, recordHistory, reason); return; }
                RevokeActorWaitingSpeech();
                m_ActorWaitingLanguage = language;
                m_ActorWaitSpeech[m_CurrentSpeechRoundId] = new ActorWaitSpeechLease {
                    id = waiting.Id, revision = waiting.Revision, outputRevision = outputRevision };
                Debug.Log($"[ActorIntent/Waiting] task={waiting.Id} action={waiting.Action} state=waiting sequence={m_ActorInputSequence}", this);
                CompleteFastLoopReply(model, prompt, draft.Text, language, generation, recordHistory);
                return;
            }
            if (decision.Kind == "claim")
            {
                if (decision.Domain == "other_action")
                {
                    HandoffFastLoopToPlanner(model, generation, outputRevision, draft, decision, "actor-verify-claim");
                    return;
                }
                var task = m_FastLoop.HeadTask;
                bool matched = snapshot != null && task != null && snapshot.Id == task.Id && snapshot.Revision == task.Revision &&
                    (decision.Action == "head.hold" || decision.Action == task.Candidate) &&
                    m_FastLoop.CanPublishCompletion(task.Id, task.Revision, outputRevision) && IsFastHeadPhysicallyHeld(task.Id, task.Revision);
                if (matched) PublishActorHeadSpeech(model, prompt, draft.Text, language, generation, recordHistory, task, outputRevision);
                else CompleteActorCorrection(model, prompt, language, generation, recordHistory, "unsupported-completion-claim");
                return;
            }
            if (decision.Kind == "replan")
            {
                RememberActorTaskLanguage(generation, language);
                if (TryDispatchActorRecordingSinging(m_FastLoopTurn, draft, decision, language, generation)) return;
                if (TryDispatchActorKnownTask(m_FastLoopTurn, draft, decision, language, generation, userTaskBinding)) return;
                // Keep the accepted user lease and bound observations alive. A
                // proposal is neither published speech nor an execution receipt.
                HandoffFastLoopToPlanner(model, generation, outputRevision, draft, decision, "actor-replan");
                return;
            }
            var now = m_FastLoop.HeadTask;
            if (snapshot?.Id != now?.Id || (snapshot?.Revision ?? 0) != (now?.Revision ?? 0))
            { CompleteActorCorrection(model, prompt, language, generation, recordHistory, "head-changed-during-judgement"); return; }
            if (decision.Kind == "cancel")
            {
                bool cancelledWait = CancelActorWaiting("actor-cancelled");
                if (m_FastLoop.TryApplyActor(decision, draft, now?.Id, now?.Revision ?? 0, out _, out reason))
                {
                    m_SceneAttention?.Release(FastHeadOwner(now.Id, now.Revision), "actor-cancelled");
                    RevokeFastHeadSpeech("actor-cancelled");
                    CompleteFastLoopReply(model, prompt, FastHeadReceipt(language, "released"), language, generation, recordHistory);
                }
                else if (!m_SceneActionActive && !string.IsNullOrEmpty(legacyOwner) &&
                    m_PersistentSceneAttention?.active == true && m_PersistentSceneAttention.actionId == legacyOwner &&
                    m_SceneAttention != null && m_SceneAttention.OwnerId == legacyOwner && !m_SceneAttention.IsReturning)
                {
                    EndPersistentSceneAttention(m_PersistentSceneAttention, "actor-cancelled");
                    trace?.Mark("actor_intent.legacy_gaze_released", new JObject { ["draftId"] = draft.Id, ["owner"] = legacyOwner });
                    CompleteFastLoopReply(model, prompt, FastHeadReceipt(language, "released"), language, generation, recordHistory);
                }
                else if (cancelledWait)
                    CompleteFastLoopReply(model, prompt, ActorWaitingCancelledText(language), language, generation, recordHistory);
                else CompleteActorCorrection(model, prompt, language, generation, recordHistory, reason);
                return;
            }
            if (decision.Action != "head.hold") CancelActorWaiting("actor-chose-new-head-task");
            BeginActorHeadTask(decision, draft, model, prompt, language, generation, recordHistory, outputRevision, draft.Text);
        }
        if (!Current()) return;
        backend.PostActorIntent(intentContext, draft.Text, currentUser, (decision, status) => {
            if (consumed || !Current()) return;
            consumed = true;
            bool valid = ActorIntentProtocol.Validate(decision, draft.Text, out string invalid);
            bool prospectiveNone = valid && decision.Kind == "none" && ActorIntentProtocol.NeedsNonActionReview(draft.Text);
            bool knownConditional = valid && ActorIntentProtocol.NeedsKnownTaskConditionReview(decision, draft.Text, currentUser);
            bool needsReview = !valid || decision.Kind == "claim" || prospectiveNone || knownConditional;
            if (!needsReview) { ApplyDecision(decision, status); return; }
            // Legacy backends can still verify a valid typed claim, but cannot
            // recover an uncertain classification into an executable proposal.
            if (!(backend is IActorIntentReviewBackend reviewer))
            {
                if (valid && !prospectiveNone && !knownConditional) ApplyDecision(decision, status);
                else CompleteActorInterpretationFailure(model, prompt, language, generation, recordHistory,
                    prospectiveNone || knownConditional ? "actor-prospective-review-unavailable" : decision?.ReviewReason ?? (decision == null ? status : invalid));
                return;
            }
            string reviewReason = knownConditional ? "known-task-deferred-clause" : prospectiveNone ? "prospective-none-consistency" : valid ? "claim-temporal-review" : decision?.ReviewReason ?? (decision == null ? status : invalid);
            trace?.Mark("actor_intent.review_required", new JObject {
                ["draftId"] = draft.Id, ["reason"] = reviewReason,
                ["domain"] = decision?.Domain, ["kind"] = decision?.Kind,
                ["task"] = decision?.Task, ["coverage"] = decision?.Coverage,
                ["executionAuthorized"] = false });
            if (!Current()) return;
            bool reviewConsumed = false;
            try
            {
                // Do not feed the failed label back as semantic evidence: the
                // reviewer independently reads the same draft and referents.
                reviewer.PostActorIntentReview(intentContext, draft.Text, currentUser, (review, reviewStatus) => {
                    if (reviewConsumed || !Current()) return;
                    reviewConsumed = true;
                    bool reviewed = ActorIntentProtocol.CompileReview(review, draft.Text, out var resolved, out string failure);
                    if (reviewed && ActorIntentProtocol.ReviewContradictsTypedCommitment(decision, review, draft.Text))
                    { reviewed = false; failure = "actor_review_contradicts_typed_commitment"; }
                    trace?.Mark("actor_intent.review_resolved", new JObject {
                        ["draftId"] = draft.Id, ["accepted"] = reviewed, ["status"] = review?.Status,
                        ["trigger"] = review?.Trigger, ["task"] = review?.Task, ["coverage"] = review?.Coverage,
                        ["reason"] = review == null ? reviewStatus : failure,
                        ["seconds"] = Time.realtimeSinceStartup - started,
                        ["evidenceCharacters"] = review?.Evidence?.Length ?? 0 });
                    if (!Current()) return;
                    if (!reviewed)
                    { CompleteActorInterpretationFailure(model, prompt, language, generation, recordHistory,
                        review == null ? reviewStatus : failure); return; }
                    // Preserve typed physical verification only after the review
                    // independently confirms this is a report, not an intention.
                    if (valid && decision.Kind == "claim" && decision.Domain == "head" && review.Status == "reported_action")
                        resolved = decision;
                    ApplyDecision(resolved, "semantic-review:" + review.Status);
                }, trace);
            }
            catch (Exception)
            {
                if (reviewConsumed || !Current()) return;
                reviewConsumed = true;
                CompleteActorInterpretationFailure(model, prompt, language, generation, recordHistory, "actor-review-provider-error");
            }
        }, trace);
    }

    private static string ActorEvidenceExcerpt(string evidence) => string.IsNullOrEmpty(evidence) ? "" :
        evidence.Length <= 120 ? evidence : evidence.Substring(0, 120) + "…";

    private void BeginActorHeadTask(ActorIntentDecision decision, ActorDraft draft, LLM model, string prompt,
        string language, int generation, bool recordHistory, long outputRevision, string speech)
    {
        BeginActorHeadTaskCore(decision, draft, model, prompt, language, generation, recordHistory, outputRevision, speech, true);
    }

    private void BeginActorHeadTaskCore(ActorIntentDecision decision, ActorDraft draft, LLM model, string prompt,
        string language, int generation, bool recordHistory, long outputRevision, string speech, bool publishOnAcceptance)
    {
        var previous = m_FastLoop.HeadTask;
        if (!m_FastLoop.TryApplyActor(decision, draft, previous?.Id, previous?.Revision ?? 0, out var task, out string reason))
        { CompleteActorCorrection(model, prompt, language, generation, recordHistory, reason); return; }
        RevokeFastHeadSpeech("actor-head-revised");
        Debug.Log($"[ActorIntent/Accepted] draft={draft.Id} task={task.Id} revision={task.Revision} action={task.Candidate}", this);
        if (task.State == "holding")
        {
            // Idempotent hold already has a physical completion fact. Do not
            // downgrade its receipt into a merely accepted movement intention.
            if (IsFastHeadPhysicallyHeld(task.Id, task.Revision))
                PublishActorHeadSpeech(model, prompt, speech, language, generation, recordHistory, task, outputRevision);
            else
            {
                m_FastLoop.Fail(task.Id, task.Revision, "head-controller-owner-lost");
                CompleteActorCorrection(model, prompt, language, generation, recordHistory, "head-controller-owner-lost");
            }
            return;
        }
        bool speechPublished = false;
        // An act draft expresses an intention: physical controller acceptance is
        // enough to publish it while the movement settles. A condition-triggered
        // completion receipt retains the stronger settlement requirement.
        StartCoroutine(RunFastHeadTaskCore(task.Id, task.Revision, decision.Action, model, prompt, language,
            generation, recordHistory, outputRevision, (success, failure) => {
                if (generation != m_FormalResponseGeneration || outputRevision != m_FastLoop.OutputRevision) return;
                if (success)
                {
                    if (!publishOnAcceptance && !speechPublished)
                        speechPublished = PublishActorHeadSpeech(model, prompt, speech, language, generation,
                            recordHistory, task, outputRevision);
                }
                else if (!speechPublished)
                    CompleteActorCorrection(model, prompt, language, generation, recordHistory, failure);
                // After publication the supervisor revokes remaining speech and
                // records failure feedback; it must not append another response
                // or history entry to the same now-silenced speech round.
            }, true, publishOnAcceptance ? (Action)(() => {
                if (!speechPublished)
                    speechPublished = PublishActorHeadSpeech(model, prompt, speech, language, generation,
                        recordHistory, task, outputRevision, false);
            }) : null));
    }

    private bool PublishActorHeadSpeech(LLM model, string prompt, string speech, string language, int generation,
        bool recordHistory, FastLoopTask task, long outputRevision, bool requireSettled = true)
    {
        if (generation != m_FormalResponseGeneration || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy ||
            !ReferenceEquals(model, m_ChatSettings?.m_ChatModel) || m_SilencedSpeechRoundIds.Contains(m_CurrentSpeechRoundId) ||
            !CanPublishFastHeadSpeech(task.Id, task.Revision, outputRevision, requireSettled)) return false;
        m_FastHeadSpeech[m_CurrentSpeechRoundId] = new FastHeadSpeechLease {
            taskId = task.Id, revision = task.Revision, outputRevision = outputRevision, requireSettled = requireSettled };
        CompleteFastLoopReply(model, prompt, speech, language, generation, recordHistory);
        return true;
    }

    private bool TryDispatchActorConditionProbe(LLM model, string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext)
    {
        var waiting = m_ActorWaiting.Pending;
        if (!m_EnableActorIntentCompiler || waiting?.State != "waiting" || m_ActorConditionObservedGeneration == generation ||
            !(model is IActorIntentBackend backend) || !backend.SupportsActorIntents) return false;
        m_ActorConditionObservedGeneration = generation;
        m_FirstReplyAttemptGeneration = generation;
        m_FastLoopDispatchGeneration = generation;
        m_FormalResponseInFlight = true;
        BeginActorAwareHistory(model, prompt, imageUrl, generation, recordHistory);
        CancelActorIntentRequest();
        m_ActorIntentBackend = backend;
        int serial = m_ActorIntentRequestSerial, epoch = m_WorkEpoch;
        long outputRevision = m_FastLoop.BeginTurn(), sequence = m_ActorInputSequence;
        bool consumed = false;
        string currentUser = FastLoopCurrentUserText(prompt);
        backend.PostActorConditionSignal(BuildActorIntentContext(), currentUser, (signal, status) => {
            if (consumed || !ActorRequestCurrent(model, generation, epoch, outputRevision, serial)) return;
            consumed = true;
            bool fired = m_ActorWaiting.Observe(waiting.Id, waiting.Revision, sequence, signal, out var trigger);
            RevokeActorWaitingSpeech();
            Debug.Log($"[ActorIntent/Condition] task={waiting.Id} sequence={sequence} signal={signal} fired={fired} status={status}", this);
            if (fired)
            {
                var draft = new ActorDraft { Id = trigger.DraftId, Revision = trigger.DraftRevision, Text = trigger.DraftText };
                var decision = new ActorIntentDecision { Domain = "head", Kind = "act", Action = trigger.Action, Condition = "none", Evidence = trigger.Evidence, MotionScope = "head_preset" };
                // One consumed condition event owns this turn. It cannot also ask
                // the actor to submit a second copy of the same relative movement.
                string language = m_ActorWaitingLanguage;
                BeginActorHeadTaskCore(decision, draft, model, prompt, language, generation, recordHistory, outputRevision,
                    FastHeadReceipt(language, "holding"), false);
            }
            else
            {
                var currentWait = m_ActorWaiting.Pending;
                bool consumedCount = (signal == "one" || signal == "two" || signal == "three") &&
                    currentWait?.Id == waiting.Id && currentWait.Revision == waiting.Revision &&
                    currentWait.State == "waiting" && currentWait.LastSequence == sequence;
                if (consumedCount)
                {
                    // A bounded counting event advances the existing plan. Starting
                    // another role reply here lets the character count on the user's
                    // behalf and replace its still-unmet plan with a premature act.
                    m_ResponseTrace?.Mark("actor_condition.progress", new JObject {
                        ["taskId"] = currentWait.Id, ["revision"] = currentWait.Revision,
                        ["sequence"] = sequence, ["progress"] = currentWait.Progress,
                        ["signal"] = signal, ["publicReply"] = false });
                    CompleteFastLoopReply(model, prompt, "", m_ActorWaitingLanguage, generation, recordHistory);
                }
                else TryDispatchFastTaskLoop(model, prompt, imageUrl, generation, recordHistory, transientContext);
            }
        }, m_ResponseTrace?.Child("actor-condition"));
        return true;
    }

    private bool CancelActorWaiting(string reason)
    {
        bool cancelled = m_ActorWaiting.Cancel(reason);
        if (cancelled) RevokeActorWaitingSpeech();
        return cancelled;
    }

    private bool ValidateActorWaitingSpeech(int roundId)
    {
        if (!m_ActorWaitSpeech.TryGetValue(roundId, out var lease)) return true;
        var task = m_ActorWaiting.Pending;
        if (task != null && task.State == "waiting" && task.Id == lease.id && task.Revision == lease.revision &&
            m_FastLoop.OutputRevision == lease.outputRevision) return true;
        DiscardPendingSpeechForRound(roundId, "actor-waiting-plan-invalidated");
        m_ActorWaitSpeech.Remove(roundId);
        return false;
    }

    private void RevokeActorWaitingSpeech()
    {
        foreach (int round in new List<int>(m_ActorWaitSpeech.Keys)) ValidateActorWaitingSpeech(round);
    }

    private void CompleteActorCorrection(LLM model, string prompt, string language, int generation, bool recordHistory, string reason)
    {
        if (generation != m_FormalResponseGeneration) return;
        m_PendingToolCorrectionFrame = "[角色意图执行反馈] " + reason + "；原草稿未获执行事实支持，已替换公开台词，禁止声称动作成功。";
        Debug.LogWarning($"[ActorIntent/Correction] generation={generation} reason={reason}", this);
        CompleteFastLoopReply(model, prompt, ActorCorrectionText(language, false), language, generation, recordHistory);
    }

    private void CompleteActorInterpretationFailure(LLM model, string prompt, string language, int generation, bool recordHistory, string reason)
    {
        CompleteOwnedReplyFailure(model, prompt, language, generation, recordHistory, reason, true);
    }

    private void CompleteOwnedReplyFailure(LLM model, string prompt, string language, int generation,
        bool recordHistory, string reason, bool requiresActorCompiler)
    {
        if (generation != m_FormalResponseGeneration) return;
        int serial = m_ActorIntentRequestSerial, epoch = m_WorkEpoch;
        long outputRevision = m_FastLoop.OutputRevision;
        // Fast routing also needs a bounded public failure when its optional
        // Actor compiler is disabled. Only that feature flag is relaxed; the
        // same input/provider/output/serial lease remains mandatory.
        bool Current() => serial == m_ActorIntentRequestSerial && generation == m_FormalResponseGeneration &&
            epoch == m_WorkEpoch && outputRevision == m_FastLoop.OutputRevision && isActiveAndEnabled &&
            !m_SceneVisionApplicationPaused && !m_UserSpeechActiveForAutonomy && m_EnableFastTaskLoop &&
            (!requiresActorCompiler || m_EnableActorIntentCompiler) && ReferenceEquals(model, m_ChatSettings?.m_ChatModel);
        if (!Current() || ActorReplyFailureAlreadyClosed(model, generation)) return;
        // Claim before trace/cancellation callbacks: repeated General entries
        // and synchronous provider callbacks cannot publish a second language.
        m_ActorReplyFailureModel = model; m_ActorReplyFailureGeneration = generation;
        m_ActorReplyFailureEpoch = epoch;
        language = ActorReplyFailureLanguage(generation, language);
        var candidate = m_ActorGeneralCandidate;
        if (candidate != null && ReferenceEquals(candidate.Model, model) && candidate.Generation == generation && candidate.Epoch == epoch)
        { candidate.Consumed = true; candidate.AwaitingPublication = false; }
        CancelActorSpeechPreparation("actor-interpretation-unresolved");
        if (!Current()) return;
        CloseToolCorrectionForActorFailure();
        if (!Current()) return;
        m_PendingToolCorrectionFrame = "[角色草稿理解状态] " + reason + "；本次回复未完成，未授权额外动作。已经核验的早先执行结果仍有效，不能被本次判定失败抹去；不能把判定失败描述成动作执行失败。";
        m_ResponseTrace?.Mark("actor_intent.interpretation_unresolved", new JObject { ["generation"] = generation, ["reason"] = reason,
            ["language"] = language, ["closure"] = "once-per-current-turn", ["newActionSubmitted"] = false });
        if (!Current()) return;
        string speech = ActorReplyFailureTextForCurrentInput(model, generation, language);
        SettleActorUserWork(generation, epoch, false, reason);
        CompleteFastLoopReply(model, prompt, speech, language, generation, recordHistory);
    }

    private LLM m_ActorReplyFailureModel;
    private int m_ActorReplyFailureGeneration = -1, m_ActorReplyFailureEpoch = -1;
    private bool ActorReplyFailureAlreadyClosed(LLM model, int generation) =>
        ReferenceEquals(m_ActorReplyFailureModel, model) && m_ActorReplyFailureGeneration == generation &&
        m_ActorReplyFailureEpoch == m_WorkEpoch;

    private string ActorReplyFailureTextForCurrentInput(LLM model, int generation, string language)
    {
        var turn = m_ActorPlanTurn;
        if (m_ActorPlan != null && turn != null && ReferenceEquals(turn.Model, model) &&
            turn.Generation == generation && turn.Epoch == m_WorkEpoch)
            foreach (var token in m_ActorPlan.Results)
                if ((bool?)token["succeeded"] == true)
                    return ActorPlanClosureText(m_ActorPlan, language, m_ActorPlan.State == "completed");
        return ActorReplyFailureText(language);
    }

    private string ActorReplyFailureLanguage(int generation, string fallback) =>
        m_ActorTaskLanguageGeneration == generation ? ActorTaskLanguageFor(generation) : RoomFallbackLanguage(fallback);

    private static string ActorReplyFailureText(string language) => language == "ja"
        ? "この返事はうまくまとめられなかったわ。新しい動作はまだ始めていないわ。"
        : language == "en" ? "I couldn't complete this reply. I haven't started a new action."
        : "这一轮我还没能给出有效回复，也没有开始新的动作。";

    private static string ActorCorrectionText(string language, bool needsPlanning) => language == "ja"
        ? (needsPlanning ? "その動きはまだ準備が必要で、実行していないよ。" : "さっきの言い方を訂正するね。その動きはまだ確認できていないよ。")
        : language == "en" ? (needsPlanning ? "That action still needs planning; I haven't started it." : "Let me correct that: I haven't confirmed that movement.")
        : needsPlanning ? "这个动作还需要进一步规划，我现在还没开始执行。" : "我更正一下：这个动作还没有确认完成。";

    private static string ActorWaitingCancelledText(string language) => language == "ja" ? "待っていた動作を取り消したよ。" :
        language == "en" ? "I've cancelled the waiting action." : "已经取消等待中的动作。";

    private static bool ActorCountThreeHasLiteralAnchor(string text)
    {
        // This is a numeric-source check after intent classification, never an
        // action detector. An ungrounded condition gets a read-only explanation.
        string normalized = (text ?? "").Normalize(System.Text.NormalizationForm.FormKC);
        return System.Text.RegularExpressions.Regex.IsMatch(normalized,
            @"(?<![0-9])3(?![0-9])|(?<![零〇一二三四五六七八九十百千万两])三(?![零〇一二三四五六七八九十百千万两])|\bthree\b|\bthird\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
