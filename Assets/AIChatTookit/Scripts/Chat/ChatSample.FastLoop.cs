using System;
using System.Collections;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("Experimental fast loop with a replaceable actor-draft intent judge and persistent head/condition tasks. Enable for controlled validation.")]
    private bool m_EnableFastTaskLoop = false;
    private FastLoopRuntime m_FastLoop = new FastLoopRuntime();
    private IFastLoopDecisionBackend m_FastLoopBackend;
    private int m_FastLoopRequestGeneration = -1;
    private int m_FastLoopDispatchGeneration = -1;
    private JObject m_FastLoopVisualFacts;
    private int m_FastLoopVisualFactsGeneration = -1;
    private FastLoopTurnContext m_FastLoopTurn;
    private readonly Dictionary<int, FastHeadSpeechLease> m_FastHeadSpeech = new Dictionary<int, FastHeadSpeechLease>();

    private sealed class FastHeadSpeechLease
    {
        public string taskId;
        public int revision;
        public long outputRevision;
        public bool requireSettled = true;
    }

    private bool TryDispatchFastTaskLoop(LLM model, string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext)
    {
        if (!m_EnableFastTaskLoop || !(model is IFastLoopDecisionBackend backend) || !backend.SupportsFastLoopDecisions)
            return false;
        CancelActorPlan("new-user-turn");
        ObserveActorInputGeneration(generation);
        if (TryDispatchActorConditionProbe(model, prompt, imageUrl, generation, recordHistory, transientContext))
            return true;
        m_FirstReplyAttemptGeneration = generation;
        m_FastLoopDispatchGeneration = generation;
        m_FastLoopBackend?.CancelFastLoopDecision();
        m_FastLoopBackend = backend;
        BeginActorAwareHistory(model, prompt, imageUrl, generation, recordHistory);
        m_FastLoopRequestGeneration = generation;
        long outputRevision = m_FastLoop.BeginTurn();
        int epoch = m_WorkEpoch;
        var turn = new FastLoopTurnContext(model, prompt, imageUrl, transientContext,
            generation, epoch, m_CurrentSpeechRoundId, outputRevision, recordHistory);
        m_FastLoopTurn = turn;
        foreach (var priorRound in new List<int>(m_FastTaskControlSpeech.Keys))
            if (priorRound != turn.Round) m_FastTaskControlSpeech.Remove(priorRound);
        turn.BodyControl = CaptureFastTaskControl();
        string taskId = m_FastLoop.HeadTask?.Id;
        int taskRevision = m_FastLoop.HeadTask?.Revision ?? 0;
        bool consumed = false;
        var observation = GetFormalSceneObservation(generation);
        bool matchingImage = observation?.HasImage == true && !string.IsNullOrEmpty(imageUrl) &&
            string.Equals(observation.ImageDataUrl, imageUrl, StringComparison.Ordinal);
        bool imageFresh = matchingImage && IsFreshSceneObservation(observation);
        string fastImage = imageFresh ? imageUrl : null;
        m_FastLoopVisualFactsGeneration = generation;
        m_FastLoopVisualFacts = new JObject {
            ["source"] = "character-scene-rgb", ["imageAttached"] = imageFresh,
            ["state"] = !SceneEyesOpen ? "eyes-closed" : imageFresh ? "ready" : matchingImage ? "stale" : "unavailable",
            ["captureId"] = matchingImage ? observation.CaptureId : "",
            ["ageSeconds"] = matchingImage ? Time.realtimeSinceStartup - observation.CapturedAtRealtime : -1f,
            ["freshAtDispatch"] = imageFresh,
            ["scope"] = "Only the image actually attached to this role request is visual evidence. Eye/head state and previous dialogue are not current scene contents."
        };
        bool Current() => !consumed && ReferenceEquals(m_FastLoopTurn, turn) && m_FastLoopRequestGeneration == generation &&
            outputRevision == m_FastLoop.OutputRevision && generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
            isActiveAndEnabled && !m_SceneVisionApplicationPaused && !m_UserSpeechActiveForAutonomy &&
            ReferenceEquals(model, m_ChatSettings?.m_ChatModel);
        m_FormalResponseInFlight = true;
        PublishSpokenPrefixToLlm();
        var trace = m_ResponseTrace?.Child("fast-task-loop");
        float started = Time.realtimeSinceStartup;
        trace?.Mark("fast_loop.dispatch", new JObject { ["generation"] = generation, ["taskId"] = taskId,
            ["taskRevision"] = taskRevision, ["outputRevision"] = outputRevision,
            ["vision"] = m_FastLoopVisualFacts.DeepClone() });
        if (!Current()) return true;
        string currentUser = FastLoopCurrentUserText(prompt);
        string fastContext = BuildFastLoopTurnContext(turn).ToString(Formatting.None);
        backend.PostFastLoopDecision(fastContext, currentUser, (decision, status) => {
            if (!Current()) return;
            consumed = true;
            m_FastLoopRequestGeneration = -1;
            trace?.Mark("fast_loop.decision", new JObject { ["route"] = decision?.Route, ["candidate"] = decision?.Candidate,
                ["taskControl"] = decision?.TaskControl,
                ["status"] = status, ["seconds"] = Time.realtimeSinceStartup - started });
            Debug.Log($"[FastLoop/Decision] generation={generation} route={decision?.Route} candidate={decision?.Candidate} status={status} seconds={Time.realtimeSinceStartup - started:F3}", this);
            ApplyFastLoopAnswer(turn, decision, currentUser, taskId, taskRevision, trace, status);
        }, trace, fastImage);
        return true;
    }

    private void ApplyFastLoopAnswer(FastLoopTurnContext turn, FastLoopDecision decision,
        string currentUser, string taskId, int taskRevision, ConversationTrace trace, string decisionStatus = null)
    {
        if (!FastLoopTurnCurrent(turn)) return;
        var model = turn.Model;
        string prompt = turn.Prompt;
        int generation = turn.Generation;
        long outputRevision = turn.OutputRevision;
        bool recordHistory = turn.RecordHistory;
        if (decision != null && !FastLoopProtocol.Validate(decision, currentUser, out string invalidReason))
        { decision = null; decisionStatus = invalidReason; }
        // A rejected response supplies no domain or action choice. In particular,
        // lyrics and feedback do not become spatial goals merely because the
        // model used stale control evidence. Reconsider this input once first.
        if (decision == null)
        {
            if (!TryRepairUnavailableFastDecision(turn, currentUser, taskId, taskRevision, decisionStatus))
                CompleteFastReplyRecoveryFailure(turn, m_ActorTaskLanguage, decisionStatus ?? "decision-unavailable");
            return;
        }
        if (decision != null) RememberActorTaskLanguage(generation, decision.Language);
        if (TryReviewFastTaskControl(turn, decision, currentUser, taskId, taskRevision, trace)) return;
        if (TryApplySingingSessionUpdate(turn, decision, currentUser, trace)) return;
        if (decision != null && decision.TaskControl != "none")
        {
            ApplyFastTaskControl(turn, decision);
            if (!FastLoopTurnCurrent(turn)) return;
            if (decision.Route == "reply")
            {
                if ((bool?)turn.ControlResult?["accepted"] != true)
                {
                    if (TryRepairDeclinedFastControl(turn, decision.Language, currentUser,
                        taskId, taskRevision)) return;
                    HandoffFastLoopToPlanner(model, generation, outputRevision, null, null,
                        "task-control-target-rejected");
                    return;
                }
                CompleteFastTaskControlReply(turn, decision.Language);
                return;
            }
        }
        if (decision?.Route == "recording_singing")
        {
            HandoffFastLoopToPlanner(model, generation, outputRevision, null, null,
                "typed-recording-singing", model.SupportsRecordingSingingDecision);
            return;
        }
        if (decision?.Route == "reply" && TryReviewRepeatedFastDraft(turn, decision, currentUser, taskId, taskRevision)) return;
        // This is a single owner handoff. No new-loop action or speech has been
        // submitted when the old planner is entered; the first-reply fence prevents recursion.
        if (decision.Route == "legacy" || decision.Route == "head" &&
            (m_PersistentSceneAttention?.active == true || m_SceneActionActive))
        {
            if (decision != null) RememberActorTaskLanguage(generation, decision.Language);
            HandoffFastLoopToPlanner(model, generation, outputRevision, null, null,
                "selected-full-planner");
            return;
        }
        if (decision.Route == "reply")
        {
            if (m_EnableActorIntentCompiler)
                CompileActorDraft(model, prompt, decision.Speech, decision.Language, generation, recordHistory, outputRevision);
            else CompleteFastLoopReply(model, prompt, decision.Speech, decision.Language, generation, recordHistory);
            return;
        }
        if (decision.Route == "head" && decision.Candidate != "head.hold")
            CancelActorWaiting("new-explicit-head-task");
        if (!m_FastLoop.TryApply(decision, currentUser, taskId, taskRevision, out var task, out string reason))
        {
            CompleteFastLoopReply(model, prompt, FastHeadReceipt(decision.Language, "failed"), decision.Language,
                generation, recordHistory);
            trace?.Mark("fast_loop.rejected", new JObject { ["reason"] = reason });
            return;
        }
        if (decision.Route == "stop_voice")
        {
            // The new utterance already interrupted old audio. Finishing this
            // response silently does not alter the independently held head task.
            CompleteFastLoopReply(model, prompt, "", decision.Language, generation, recordHistory);
            return;
        }
        if (decision.Candidate == "head.release")
        {
            m_SceneAttention?.Release(FastHeadOwner(task.Id, taskRevision), "fast-loop-explicit-head-stop");
            RevokeFastHeadSpeech("head-stopped");
            CompleteFastLoopReply(model, prompt, FastHeadReceipt(decision.Language, "released"), decision.Language,
                generation, recordHistory);
            return;
        }
        RevokeFastHeadSpeech("head-revised");
        // A held task is an idempotent request. It must still have its actual
        // controller owner before acknowledging it as held.
        if (task.State == "holding" && IsFastHeadPhysicallyHeld(task.Id, task.Revision))
        {
            CompleteFastHeadReply(model, prompt, decision.Language, generation, recordHistory,
                task.Id, task.Revision, outputRevision);
            return;
        }
        if (task.State == "holding")
        {
            m_FastLoop.Fail(task.Id, task.Revision, "head-controller-owner-lost");
            CompleteFastLoopReply(model, prompt, FastHeadReceipt(decision.Language, "failed"),
                decision.Language, generation, recordHistory);
            return;
        }
        StartCoroutine(RunFastHeadTask(task.Id, task.Revision, decision.Candidate,
            model, prompt, decision.Language, generation, recordHistory, outputRevision));
    }

    private string BuildFastLoopContext()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var task = m_FastLoop.HeadTask;
        bool actorDraftCompiler = m_EnableActorIntentCompiler &&
            m_ChatSettings?.m_ChatModel is IActorIntentBackend actorBackend && actorBackend.SupportsActorIntents;
        var facts = new JObject {
            ["actorDraftCompiler"] = actorDraftCompiler,
            ["actorDraftCapabilities"] = FastLoopProtocol.ActorDraftCapabilities(actorDraftCompiler),
            ["headTask"] = task == null ? null : new JObject { ["id"] = task.Id, ["revision"] = task.Revision,
                ["state"] = task.State, ["candidate"] = task.Candidate, ["reason"] = task.Reason },
            ["headAvailable"] = m_EnableSceneActions && RoomMotionOutputEnabled && SceneEyesOpen,
            ["headAngles"] = new JObject { ["yaw"] = m_SceneAttention?.CurrentHeadAngles.x ?? 0,
                ["pitch"] = m_SceneAttention?.CurrentHeadAngles.y ?? 0 },
            ["legacyGazeActive"] = m_PersistentSceneAttention?.active == true || m_SceneActionActive,
            ["bodyBusy"] = bridge != null && (bridge.ReservesBody || bridge.IsFollowing),
            ["roomTask"] = new JObject {
                ["connected"] = bridge?.IsConnected == true,
                ["activeTaskId"] = bridge?.ActiveTaskId ?? "",
                ["lastActionId"] = bridge?.ActionId ?? "",
                ["taskMode"] = bridge?.TaskMode ?? "none",
                ["followActive"] = bridge?.IsConnected == true && bridge.IsFollowing,
                ["bodyReserved"] = bridge?.ReservesBody == true,
                ["phase"] = bridge?.Phase ?? "unavailable",
                ["status"] = FastLoopPrompt.ObservationText(bridge?.Status ?? "", 180),
                ["scope"] = "Current controller observation, not an inference from past dialogue. Empty activeTaskId means no current task. " +
                    "A spoken promise does not start following. Historical action/phase does not identify a new visual object."
            },
            ["eyesOpen"] = SceneEyesOpen,
            ["vision"] = m_FastLoopVisualFactsGeneration == m_FormalResponseGeneration && m_FastLoopVisualFacts != null
                ? m_FastLoopVisualFacts.DeepClone() : new JObject { ["imageAttached"] = false, ["state"] = "unavailable" },
            ["recentDialogue"] = BuildRecentRoleDialogue(),
            ["waitingTask"] = BuildActorWaitingFacts(),
            ["executionFeedback"] = m_PendingToolCorrectionFrame,
            ["currentTaskControlResult"] = m_FastLoopTurn?.ControlResult?.DeepClone(),
            ["scope"] = "头部姿势不证明看见指定物体，普通对话不结束持续任务。reply可表达能力表内的角色任务提案，由裁判和实际控制器受理；当前用户走近/跟随只有任务受理后才开始。现有录音回唱选择recording_singing；明确停止当前跟随/移动使用独立task_control，仍保留同一句后续目标。未知物体、其他条件或未覆盖复合目标交给legacy。追问失败原因不是重做动作。"
        };
        EnrichFastLoopContext(facts);
        return facts.ToString(Formatting.None);
    }

    private static string FastHeadOwner(string id, int revision) => id + ":" + revision;
    private bool FastHeadCurrent(string id, int revision) => m_FastLoop.HeadTask?.Active == true &&
        m_FastLoop.HeadTask.Id == id && m_FastLoop.HeadTask.Revision == revision;

    private bool IsFastHeadPhysicallyHeld(string id, int revision) => FastHeadCurrent(id, revision) &&
        m_SceneAttention != null && m_SceneAttention.OwnerId == FastHeadOwner(id, revision) &&
        m_SceneAttention.IsSettled && !m_SceneAttention.IsReturning;

    private bool CanPublishFastHeadSpeech(string id, int revision, long outputRevision, bool requireSettled)
    {
        if (requireSettled)
            return m_FastLoop.CanPublishCompletion(id, revision, outputRevision) && IsFastHeadPhysicallyHeld(id, revision);
        // An intention may be spoken once the actual controller accepts it. This
        // lease proves ownership and supervision, never that the turn has finished.
        string owner = FastHeadOwner(id, revision);
        if (m_FastLoop.OutputRevision != outputRevision || !FastHeadCurrent(id, revision) ||
            m_SceneAttention == null || !m_SceneAttention.isActiveAndEnabled ||
            m_SceneAttention.OwnerId != owner || !m_SceneAttention.IsBusy || m_SceneAttention.IsReturning) return false;
        var avatar = m_SceneVisionCapture != null ? m_SceneVisionCapture.Target : null;
        var eyes = avatar != null ? avatar.GetComponent<NevaEyeTracking>() : null;
        return eyes != null && m_SceneAttention.Target == avatar && eyes.AttentionOwnerId == owner &&
            FastHeadUnavailable(eyes) == null;
    }

    private string FastHeadUnavailable(NevaEyeTracking eyes, bool allowSettledGazeHandoff = false,
        bool preserveObjectPreparationPose = false)
    {
        if (!isActiveAndEnabled || m_SceneVisionApplicationPaused || !m_EnableFastTaskLoop ||
            !m_EnableSceneActions || !RoomMotionOutputEnabled || !SceneEyesOpen) return "head-control-disabled";
        if (eyes == null || !eyes.isActiveAndEnabled || !eyes.IsInitialized ||
            eyes.Target == null || m_SceneVisionCapture == null || m_SceneVisionCapture.Target != eyes.Target)
            return "head-binding-unavailable";
        if (m_SceneActionActive && !preserveObjectPreparationPose ||
            m_PersistentSceneAttention?.active == true && !allowSettledGazeHandoff) return "legacy-head-owner-active";
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge != null && (bridge.ReservesBody || bridge.IsFollowing)) return "body-controller-has-priority";
        return null;
    }

    private IEnumerator RunFastHeadTask(string id, int revision, string candidate, LLM model, string prompt,
        string language, int generation, bool recordHistory, long outputRevision)
    {
        return RunFastHeadTaskCore(id, revision, candidate, model, prompt, language, generation,
            recordHistory, outputRevision, null, false);
    }

    private IEnumerator RunFastHeadTaskCore(string id, int revision, string candidate, LLM model, string prompt,
        string language, int generation, bool recordHistory, long outputRevision,
        Action<bool, string> outcome, bool actorOwned, Action onAccepted = null)
    {
        string owner = FastHeadOwner(id, revision);
        float motionStarted = Time.realtimeSinceStartup;
        bool completed = false;
        var capture = EnsureSceneVisionCapture();
        var avatar = capture != null ? capture.Target : null;
        var eyes = avatar != null ? avatar.GetComponent<NevaEyeTracking>() : null;
        // An actor may deliberately replace a settled gaze. Keep its old owner
        // until TryLookAt has validated and accepted the replacement.
        var priorGaze = actorOwned && m_PersistentSceneAttention?.active == true &&
            m_SceneAttention?.IsSettled == true && m_SceneAttention.OwnerId == m_PersistentSceneAttention.actionId
            ? m_PersistentSceneAttention : null;
        string failure = FastHeadUnavailable(eyes, priorGaze != null);
        try
        {
            if (failure != null) yield break;
            if (m_SceneAttention == null) m_SceneAttention = GetComponent<NevaSceneAttention>() ?? gameObject.AddComponent<NevaSceneAttention>();
            if (m_SceneAttention.Target != eyes.Target) m_SceneAttention.Configure(eyes);
            Vector2 delta = candidate == "head.raise" ? new Vector2(0, 12) :
                candidate == "head.lower" ? new Vector2(0, -12) :
                candidate == "head.left" ? new Vector2(-18, 0) :
                candidate == "head.right" ? new Vector2(18, 0) : Vector2.zero;
            // These discrete presets are runtime capabilities. The model never
            // writes joint rotations, arbitrary angles, transforms or owner IDs.
            Vector2 current = m_SceneAttention.CurrentHeadAngles;
            Vector2 desired = current + delta;
            desired.x = Mathf.Clamp(desired.x, -m_SceneAttention.maximumYawDegrees, m_SceneAttention.maximumYawDegrees);
            desired.y = Mathf.Clamp(desired.y, -m_SceneAttention.maximumDownDegrees, m_SceneAttention.maximumUpDegrees);
            delta = desired - current;
            if (candidate != "head.hold" && delta.sqrMagnitude < .25f)
            { failure = "head-limit-reached"; yield break; }
            Vector3 direction = eyes.HeadFrameRotation * Quaternion.Euler(-delta.y, delta.x, 0) * Vector3.forward;
            if (!m_SceneAttention.TryLookAt(owner, eyes.EyeOrigin + direction * 3f, out failure, 4f)) yield break;
            if (priorGaze != null) EndPersistentSceneAttention(priorGaze, "actor-chose-new-head-direction");
            Debug.Log($"[FastLoop/Head] task={id} revision={revision} state=starting candidate={candidate} acceptedSeconds={Time.realtimeSinceStartup - motionStarted:F3}", this);
            onAccepted?.Invoke();
            float deadline = Time.realtimeSinceStartup + 4.5f;
            while (FastHeadCurrent(id, revision) && !m_SceneAttention.IsSettled && Time.realtimeSinceStartup < deadline)
            {
                failure = FastHeadUnavailable(eyes);
                if (failure != null || m_SceneAttention.OwnerId != owner || m_SceneAttention.IsReturning) break;
                yield return null;
            }
            if (!FastHeadCurrent(id, revision)) yield break;
            if (failure != null || !IsFastHeadPhysicallyHeld(id, revision))
            { failure = failure ?? "head-did-not-settle"; yield break; }
            if (!m_SceneAttention.RetainPoseLease(owner) || !m_FastLoop.Confirm(id, revision))
            { failure = "head-lease-unavailable"; yield break; }
            completed = true;
            Debug.Log($"[FastLoop/Head] task={id} revision={revision} state=holding settleSeconds={Time.realtimeSinceStartup - motionStarted:F3}", this);
            if (actorOwned) outcome?.Invoke(true, "holding");
            else CompleteFastHeadReply(model, prompt, language, generation, recordHistory, id, revision, outputRevision);
            // No dialogue generation in this condition: a new conversation turn
            // invalidates speech but cannot accidentally end this physical task.
            while (FastHeadCurrent(id, revision))
            {
                // The object matcher reads the current view before acquiring a
                // replacement gaze. Do not return the old pose to neutral just
                // because that read-only preparation was registered.
                failure = FastHeadUnavailable(eyes, false, CanRetainFastPoseForObjectPreparation(owner));
                if (failure != null || !IsFastHeadPhysicallyHeld(id, revision) || !m_SceneAttention.RetainPoseLease(owner))
                { failure = failure ?? "head-controller-owner-lost"; break; }
                yield return new WaitForSecondsRealtime(.2f);
            }
        }
        finally
        {
            // Release is scoped to this revision. A late finally from an old
            // correction cannot release its successor's physical pose.
            m_SceneAttention?.Release(owner, failure ?? "head-task-ended");
            if (FastHeadCurrent(id, revision))
            {
                if (completed && HasFastHeadSceneSuccessor(owner))
                {
                    m_FastLoop.CancelHead("pose-handed-to-scene-action");
                    RevokeFastHeadSpeech("pose-handed-to-scene-action");
                    m_ResponseTrace?.Mark("head.scene_handoff", new JObject {
                        ["oldOwner"] = owner, ["newOwner"] = m_SceneAttention.OwnerId,
                        ["poseReleasedByOldOwner"] = false,
                        ["scope"] = "A validated scene action acquired the actual gaze; not an object-operation result." });
                }
                else
                {
                    m_FastLoop.Fail(id, revision, failure ?? "head-task-ended");
                    RevokeFastHeadSpeech(failure ?? "head-task-ended");
                    m_PendingToolCorrectionFrame = "[头部任务执行反馈] " + (failure ?? "head-task-ended") +
                        "；动作未完成或保持姿势已经失效。未播放的动作意向与完成宣告已撤销，不能声称仍在执行或保持；普通聊天没有取消该任务。";
                    Debug.LogWarning($"[FastLoop/Head] task={id} revision={revision} state=failed reason={failure}", this);
                    if (!completed && generation == m_FormalResponseGeneration && outputRevision == m_FastLoop.OutputRevision)
                    {
                        if (actorOwned) outcome?.Invoke(false, failure ?? "head-task-ended");
                        else CompleteFastLoopReply(model, prompt, FastHeadReceipt(language, "failed"), language, generation, recordHistory);
                    }
                }
            }
        }
    }

    private void CompleteFastHeadReply(LLM model, string prompt, string language, int generation,
        bool recordHistory, string id, int revision, long outputRevision)
    {
        if (!m_FastLoop.CanPublishCompletion(id, revision, outputRevision) ||
            !IsFastHeadPhysicallyHeld(id, revision) || generation != m_FormalResponseGeneration) return;
        m_FastHeadSpeech[m_CurrentSpeechRoundId] = new FastHeadSpeechLease {
            taskId = id, revision = revision, outputRevision = outputRevision };
        CompleteFastLoopReply(model, prompt, FastHeadReceipt(language, "holding"), language, generation, recordHistory);
    }

    private void CompleteFastLoopReply(LLM model, string prompt, string text, string language,
        int generation, bool recordHistory)
    {
        if (generation != m_FormalResponseGeneration || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy ||
            !ReferenceEquals(model, m_ChatSettings?.m_ChatModel) || m_SilencedSpeechRoundIds.Contains(m_CurrentSpeechRoundId)) return;
        if (TryCompleteActorGeneralReply(model, prompt, text, language, generation, recordHistory)) return;
        CancelClosedRoomPreparation("reply-selected");
        ResolveActorSpeechPreparation(text, language, generation);
        // Only the local compiler emits the legacy speech envelope. New decisions
        // have no model-authored tags and never enter the old action parser.
        string safe = System.Security.SecurityElement.Escape(text ?? "");
        string envelope = string.IsNullOrWhiteSpace(text) ? "<silent/>" :
            "<lang code=\"" + language + "\"/><speech mode=\"independent\"/><say>" + safe + "</say>";
        if (recordHistory && !TryCompleteActorPlanAcceptanceHistory(model, generation, envelope))
        {
            if (model is IFastLoopHistoryBackend history) history.CompleteFastLoopHistory();
            else
                model.m_DataList.Add(new LLM.SendData("user", prompt ?? ""));
            model.m_DataList.Add(new LLM.SendData("assistant", envelope));
            model.CheckHistory();
        }
        m_FormalResponseInFlight = false;
        if (m_FastLoopTurn?.Generation == generation) m_FastLoopTurn = null;
        ClearStagedFormalObservation();
        if (!string.IsNullOrWhiteSpace(text))
            OnSpeechStreamDelta(new SpeechText(text, language, "fast-task-loop", SpeechActionDependency.Independent));
        else OnStreamDelta("<silent/>");
        OnStreamComplete(envelope);
    }

    private static string FastHeadReceipt(string language, string state)
    {
        if (language == "ja") return state == "holding" ? "この頭の向きを保っているよ。" :
            state == "released" ? "頭の向きの固定を解除したよ。" : "今はその頭の動きを完了できなかったよ。";
        if (language == "en") return state == "holding" ? "I'm holding this head position." :
            state == "released" ? "I've released the head position." : "I couldn't complete that head movement.";
        return state == "holding" ? "我正保持这个头部姿势。" : state == "released" ? "已经解除头部姿势的保持。" : "这次没能完成这个头部动作。";
    }

    private bool ValidateFastHeadSpeech(int roundId)
    {
        if (!ValidateActorPlanAcceptanceSpeech(roundId)) return false;
        if (!ValidateFastTaskControlSpeech(roundId)) return false;
        if (!ValidateActorTaskSpeech(roundId)) return false;
        if (!ValidateActorWaitingSpeech(roundId)) return false;
        if (!m_FastHeadSpeech.TryGetValue(roundId, out var lease)) return true;
        if (CanPublishFastHeadSpeech(lease.taskId, lease.revision, lease.outputRevision, lease.requireSettled)) return true;
        DiscardPendingSpeechForRound(roundId, "fast-head-fact-invalidated");
        m_FastHeadSpeech.Remove(roundId);
        return false;
    }

    private void RevokeFastHeadSpeech(string reason)
    {
        RevokeActorWaitingSpeech();
        var rounds = new HashSet<int>(m_FastHeadSpeech.Keys);
        rounds.UnionWith(m_FastTaskControlSpeech.Keys);
        foreach (var round in rounds)
            if (!ValidateFastHeadSpeech(round))
                m_ResponseTrace?.Mark("fast_loop.speech_revoked", new JObject { ["speechRound"] = round, ["reason"] = reason });
    }

    private void CancelFastLoopRequest(string reason)
    {
        CancelActorPlan(reason);
        var handoff = m_FastLoopTurn;
        bool ownsPlanner = handoff != null && handoff.HandedOff &&
            handoff.Generation == m_FormalResponseGeneration &&
            ReferenceEquals(handoff.Model, m_ChatSettings?.m_ChatModel);
        bool ownedResponse = (m_FastLoopDispatchGeneration == m_FormalResponseGeneration || ownsPlanner) &&
            m_FormalResponseInFlight;
        var backend = m_FastLoopBackend;
        // Invalidate first: cancelling a provider may synchronously deliver the
        // old decision or its bounded dialogue-repair callback.
        m_FastLoopBackend = null;
        m_FastLoopRequestGeneration = -1;
        m_FastLoopTurn = null;
        m_FastLoop.RevokeOutput();
        if (ownedResponse)
        {
            ++m_FormalResponseGeneration;
            m_FormalResponseInFlight = false;
            m_StreamComplete = true;
            m_TTSSenderDone = true;
            m_AgentRoundInFlight = false;
            ClearStagedFormalObservation();
        }
        CancelActorIntentRequest();
        backend?.CancelFastLoopDecision();
        RevokeFastHeadSpeech(reason);
        if (ownedResponse && ownsPlanner) handoff.Model.CancelActiveResponse();
    }

    private void CancelFastHeadTask(string reason)
    {
        if (!IsDialogueOnlySceneCancellation(reason)) CancelActorWaiting(reason);
        var task = m_FastLoop.HeadTask;
        string owner = task != null ? FastHeadOwner(task.Id, task.Revision) : null;
        if (m_FastLoop.CancelHead(reason))
        {
            m_SceneAttention?.Release(owner, reason);
            RevokeFastHeadSpeech(reason);
        }
    }
}
