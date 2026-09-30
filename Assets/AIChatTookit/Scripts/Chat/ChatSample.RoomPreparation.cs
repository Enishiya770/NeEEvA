using System;
using System.Collections;
using UnityEngine;

public partial class ChatSample
{
    // Prepare semantics while final ASR runs. Only a matching terminal transcript
    // can commit a stop; closed partials alone never acquire execution authority.
    private sealed class RoomSpeechPreparation
    {
        public int capture, inputRevision, workEpoch, operationRevision, targetRevision, expectedUserTurn;
        public bool userTurnClaimed, acceptedWorkEpoch, followingTask;
        public string text, normalized, taskId, json, status, stopActionId, stopDispatch;
        public float closedAt, deadline;
        public bool completed, accepted, finalConfirmed, consumed;
        public ArdyRoomDialogueBridge bridge;
        public LLM provider;
        public SenseVoiceSpeechToText speechProvider;
        public ConversationTrace trace;
        public RoomTaskProtocol.Decision decision;
        public Action continuation;
        public Action fallback;
        public int waitingFormalGeneration = -1;
        public float submittedAt = -1f;
    }
    private RoomSpeechPreparation m_RoomSpeechPreparation;
    private int m_RoomPreparationSequence;

    public void AcceptClosedSpeechEvidence(ClosedSpeechEvidence evidence)
    {
        // This loop discards user-only preparation when it chooses a reply or
        // binds its later adopted goal. It cannot reuse that earlier decision;
        // don't compete for inference capacity with the actual fast/actor work.
        // Legacy providers/routes retain their bounded background preparation.
        if (m_EnableFastTaskLoop && m_EnableActorIntentCompiler &&
            m_ChatSettings?.m_ChatModel is IFastLoopDecisionBackend fast && fast.SupportsFastLoopDecisions) return;
        if (!HasRoomTaskPlanner || !m_AutoSend || !enabled || !gameObject.activeInHierarchy || evidence == null ||
            m_ChatSettings.m_ChatModel.SupportsRoomPreparationMessages != true ||
            evidence.CaptureSessionSerial <= 0 || string.IsNullOrWhiteSpace(evidence.Text) || evidence.Text.Length > 160 ||
            evidence.NormalizedText != ClosedSpeechEvidence.Normalize(evidence.Text) ||
            evidence.StableForSeconds < .35f || Time.realtimeSinceStartup - evidence.ClosedAt > 1f) return;
        var speechProvider = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        if (speechProvider == null || speechProvider.InputCaptureSessionSerial != evidence.CaptureSessionSerial ||
            !m_ChatSettings.m_ChatModel.isActiveAndEnabled) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (m_RoomSpeechPreparation != null && m_RoomSpeechPreparation.capture == evidence.CaptureSessionSerial &&
            m_RoomSpeechPreparation.inputRevision == m_InputAudioRevision) return;
        CancelClosedRoomPreparation("new-closed-recording");
        CancelSceneVisionStopPreparation();
        var current = bridge.ReadCurrentTargetEvidence();
        var prepared = new RoomSpeechPreparation {
            capture = evidence.CaptureSessionSerial, inputRevision = m_InputAudioRevision, workEpoch = m_WorkEpoch,
            operationRevision = bridge.Controller.OperationRevision, targetRevision = current.currentTargetRevision,
            taskId = bridge.ActiveTaskId, bridge = bridge, text = evidence.Text, followingTask = bridge.IsFollowing,
            expectedUserTurn = m_ActionConstraints.CurrentUserTurn,
            normalized = evidence.NormalizedText, closedAt = evidence.ClosedAt, status = "pending",
            deadline = Time.realtimeSinceStartup + RoomPreparationBudget.BackgroundTimeoutSeconds, provider = m_ChatSettings.m_ChatModel,
            speechProvider = speechProvider,
            trace = (m_CaptureTrace ?? m_PendingInputTrace ?? m_ResponseTrace)?.Child("room-preparation"),
            submittedAt = Time.realtimeSinceStartup
        };
        m_RoomSpeechPreparation = prepared;
        prepared.trace?.Mark("room.preparation_send", new Newtonsoft.Json.Linq.JObject {
            ["captureSerial"] = prepared.capture,
            ["inputRevision"] = prepared.inputRevision,
            ["targetRevision"] = prepared.targetRevision,
            ["followingTask"] = prepared.followingTask,
            ["bodyReserved"] = bridge.ReservesBody,
            ["sceneEyesOpen"] = SceneEyesOpen,
            ["deadlineSeconds"] = RoomPreparationBudget.BackgroundTimeoutSeconds,
            ["formalJoinSeconds"] = RoomPreparationBudget.FormalJoinSeconds,
            ["scope"] = "request-local room decision; no execution authority before final ASR exact match"
        });
        Debug.Log($"[Room/Preparation] capture={prepared.capture} input={prepared.inputRevision} task={prepared.taskId} operation={prepared.operationRevision} state=submitted", this);
        m_ChatSettings.m_ChatModel.PostRoomPreparationMessage(BuildRoomDecisionInput(prepared.text, false, "", bridge), (ok, json, status) => {
            CheckRoomPreparationWait(prepared, Time.realtimeSinceStartup);
            if (!IsFreshRoomPreparation(prepared) || prepared.completed) return;
            prepared.completed = true;
            prepared.json = json;
            prepared.status = status;
            prepared.accepted = ok && RoomTaskProtocol.TryDecision(json, prepared.text, false, out prepared.decision);
            prepared.trace?.Mark("room.preparation_complete", new Newtonsoft.Json.Linq.JObject {
                ["captureSerial"] = prepared.capture,
                ["accepted"] = prepared.accepted,
                ["status"] = status ?? "",
                ["intent"] = prepared.decision?.intent,
                ["elapsedMs"] = prepared.submittedAt >= 0f
                    ? (double?)(Time.realtimeSinceStartup - prepared.submittedAt) * 1000.0 : null,
                ["finalConfirmed"] = prepared.finalConfirmed,
                ["scope"] = "decision receipt only; no execution authority before final ASR exact match"
            });
            CommitPreparedRoomStop(prepared);
            var continuation = prepared.continuation; prepared.continuation = null; continuation?.Invoke();
        });
        StartCoroutine(WatchRoomPreparation(prepared));
    }

    private bool IsFreshRoomPreparation(RoomSpeechPreparation prepared) => prepared != null &&
        ReferenceEquals(prepared, m_RoomSpeechPreparation) && IsRoomPreparationContextCurrent(prepared);

    private bool IsRoomPreparationContextCurrent(RoomSpeechPreparation prepared) => prepared != null &&
        enabled && gameObject.activeInHierarchy &&
        prepared.inputRevision == m_InputAudioRevision && prepared.workEpoch == m_WorkEpoch &&
        prepared.expectedUserTurn == m_ActionConstraints.CurrentUserTurn && m_AutoSend &&
        prepared.provider != null && prepared.provider.isActiveAndEnabled &&
        m_ChatSettings?.m_ChatModel == prepared.provider && prepared.speechProvider != null &&
        m_ChatSettings?.m_SpeechToText == prepared.speechProvider &&
        prepared.speechProvider.InputCaptureSessionSerial == prepared.capture &&
        prepared.bridge != null && prepared.bridge.IsConnected &&
        GetComponent<ArdyRoomDialogueBridge>() == prepared.bridge;

    // SetCurrentUserInput resets the work epoch BEFORE the accepted user ledger
    // advances. Transfer both identities exactly once for this confirmed ASR
    // result; a new typed input or another work reset cannot inherit the permit.
    private RoomSpeechPreparation BeginPreparedRoomAcceptedInput(string text)
    {
        var prepared = m_RoomSpeechPreparation;
        if (prepared == null) return null;
        if (!IsFreshRoomPreparation(prepared) || !prepared.finalConfirmed || prepared.acceptedWorkEpoch ||
            prepared.userTurnClaimed || ClosedSpeechEvidence.Normalize(text) != prepared.normalized)
        { CancelClosedRoomPreparation("different-accepted-input"); return null; }
        prepared.acceptedWorkEpoch = true;
        return prepared;
    }

    private void CompletePreparedRoomAcceptedInput(RoomSpeechPreparation prepared)
    {
        if (prepared == null || !ReferenceEquals(prepared, m_RoomSpeechPreparation)) return;
        if (m_WorkEpoch != checked(prepared.workEpoch + 1))
        { CancelClosedRoomPreparation("unexpected-work-epoch-change"); return; }
        prepared.workEpoch = m_WorkEpoch;
        if (!IsFreshRoomPreparation(prepared)) CancelClosedRoomPreparation("accepted-input-state-changed");
    }

    private void ClaimPreparedRoomUserTurn(string text)
    {
        var prepared = m_RoomSpeechPreparation;
        if (prepared == null) return;
        if (!IsFreshRoomPreparation(prepared) || !prepared.finalConfirmed || !prepared.acceptedWorkEpoch || prepared.userTurnClaimed ||
            ClosedSpeechEvidence.Normalize(text) != prepared.normalized)
        { CancelClosedRoomPreparation("different-accepted-user-turn"); return; }
        prepared.userTurnClaimed = true;
        prepared.expectedUserTurn = checked(m_ActionConstraints.CurrentUserTurn + 1);
    }

    private IEnumerator WatchRoomPreparation(RoomSpeechPreparation prepared)
    {
        while (ReferenceEquals(prepared, m_RoomSpeechPreparation) && !prepared.consumed)
        {
            yield return null;
            CheckRoomPreparationWait(prepared, Time.realtimeSinceStartup);
        }
    }

    private void CheckRoomPreparationWait(RoomSpeechPreparation prepared, float now)
    {
        if (prepared == null || !ReferenceEquals(prepared, m_RoomSpeechPreparation)) return;
        if (!IsFreshRoomPreparation(prepared))
        { CancelClosedRoomPreparation("preparation-state-changed"); return; }
        if (prepared.completed) return;
        if (now < prepared.deadline) return;
        var fallback = prepared.waitingFormalGeneration == m_FormalResponseGeneration ? prepared.fallback : null;
        bool priorRoundInFlight = m_AgentRoundInFlight, priorStreamComplete = m_StreamComplete;
        // Detach before starting a fresh request. A cancelled provider is allowed
        // never to invoke its callback; a late success must not acquire authority.
        CancelClosedRoomPreparation("preparation-deadline");
        if (fallback != null && m_RoomSpeechPreparation == null && IsRoomPreparationContextCurrent(prepared) &&
            prepared.waitingFormalGeneration == m_FormalResponseGeneration)
        {
            // The replacement request continues this same reply. Cancel clears
            // these flags for a true cancellation; a bounded retry must retain
            // the original agent/stream completion state.
            m_AgentRoundInFlight = priorRoundInFlight;
            m_StreamComplete = priorStreamComplete;
            m_FormalResponseInFlight = true;
            fallback();
        }
    }

    private void ConfirmClosedRoomSpeechFinal(string callbackText)
    {
        var prepared = m_RoomSpeechPreparation;
        if (!IsFreshRoomPreparation(prepared)) return;
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        string source = sense != null ? sense.LastFinalTranscriptSource : "";
        string finalText = source == "closed-stream-recovery" ? sense?.LastText : sense?.LastFinalRawText;
        bool confirmed = !string.IsNullOrWhiteSpace(callbackText) && sense != null &&
            sense.LastFinalCaptureSerial == prepared.capture &&
            (source == "full-asr" || source == "full-preview" || source == "closed-stream-recovery") &&
            sense.MatchesFinalTranscriptCallback(prepared.capture, callbackText) &&
            !string.IsNullOrWhiteSpace(finalText) && ClosedSpeechEvidence.Normalize(finalText) == prepared.normalized;
        if (!confirmed)
        {
            CancelClosedRoomPreparation("final-rejected-or-revised");
            return;
        }
        prepared.finalConfirmed = true;
        prepared.trace?.Mark("room.preparation_final_confirmed", new Newtonsoft.Json.Linq.JObject {
            ["captureSerial"] = prepared.capture, ["source"] = source,
            ["rawCharacters"] = finalText.Length, ["callbackCharacters"] = callbackText.Length,
            ["scope"] = "exact same-capture final raw transcript and bound delivered envelope; metadata is not user text"
        });
        Debug.Log($"[Room/Preparation] capture={prepared.capture} task={prepared.taskId} state=final-confirmed source={source} sinceClose={Time.realtimeSinceStartup-prepared.closedAt:F3}s", this);
        CommitPreparedRoomStop(prepared);
    }

    private void CommitPreparedRoomStop(RoomSpeechPreparation prepared)
    {
        if (!IsFreshRoomPreparation(prepared) || !prepared.completed || !prepared.accepted || !prepared.finalConfirmed ||
            prepared.decision.intent != "stop-moving" || prepared.stopActionId != null) return;
        prepared.stopActionId = m_MotionIdentityPrefix + ":capture:" + prepared.capture + ":stop:" + (++m_RoomPreparationSequence);
        bool stopped = prepared.bridge.StopForTaskIfCurrent(prepared.stopActionId, prepared.taskId,
            prepared.operationRevision, out string failure, prepared.followingTask);
        prepared.stopDispatch = stopped ? "stop-submitted" : "not-submitted: " + failure;
        Debug.Log($"[Room/QuickStop] capture={prepared.capture} expectedTask={prepared.taskId} expectedOperation={prepared.operationRevision} action={prepared.stopActionId} dispatch={prepared.stopDispatch} sinceClose={Time.realtimeSinceStartup-prepared.closedAt:F3}s", this);
    }

    private void CancelClosedRoomPreparation(string reason)
    {
        var prepared = m_RoomSpeechPreparation;
        m_RoomSpeechPreparation = null;
        if (prepared == null) return;
        if (prepared.waitingFormalGeneration == m_FormalResponseGeneration && prepared.continuation != null)
        {
            m_FormalResponseInFlight = false;
            m_StreamComplete = true;
            m_AgentRoundInFlight = false;
        }
        prepared.continuation = null;
        prepared.fallback = null;
        prepared.provider?.CancelRoomPreparationMessage();
        prepared.trace?.Mark("room.preparation_discarded", new Newtonsoft.Json.Linq.JObject {
            ["captureSerial"] = prepared.capture,
            ["inputRevision"] = prepared.inputRevision,
            ["reason"] = reason ?? "unknown",
            ["completed"] = prepared.completed,
            ["accepted"] = prepared.accepted,
            ["finalConfirmed"] = prepared.finalConfirmed,
            ["consumed"] = prepared.consumed,
            ["intent"] = prepared.decision?.intent,
            ["scope"] = "prefetch discarded or superseded; no execution authority granted by this event"
        });
        Debug.Log($"[Room/Preparation] capture={prepared.capture} state=discarded reason={reason}", this);
    }

    private bool TryConsumeRoomPreparation(int generation, ArdyRoomDialogueBridge bridge,
        Action<bool, string, string, string, RoomSpeechPreparation> accept, Action requestFresh)
    {
        var prepared = m_RoomSpeechPreparation;
        CheckRoomPreparationWait(prepared, Time.realtimeSinceStartup);
        if (!IsFreshRoomPreparation(prepared) || !prepared.finalConfirmed || prepared.consumed ||
            !ReferenceEquals(bridge, prepared.bridge)) return false;
        void Deliver()
        {
            if (!IsFreshRoomPreparation(prepared) || generation != m_FormalResponseGeneration || prepared.consumed) return;
            prepared.consumed = true;
            // An early closed-transcript decision has no scene observation.
            // Preserve non-visual decisions (including ordinary `none`) without
            // making the old no-image result authoritative for a visual intent.
            if (SceneEyesOpen && prepared.decision?.IsVisual == true)
            {
                prepared.trace?.Mark("room.preparation_discarded", new Newtonsoft.Json.Linq.JObject {
                    ["captureSerial"] = prepared.capture,
                    ["generation"] = generation,
                    ["reason"] = "visual-requires-fresh-observation",
                    ["intent"] = prepared.decision?.intent,
                    ["scope"] = "confirmed decision retained as a hint; no visual execution authority reused"
                });
                if (IsFreshRoomPreparation(prepared) && generation == m_FormalResponseGeneration) requestFresh();
                return;
            }
            bool targetSensitiveDecision = prepared.decision?.intent == "approach" ||
                prepared.decision?.intent == "follow";
            if (!prepared.accepted || (targetSensitiveDecision &&
                bridge.ReadCurrentTargetEvidence().currentTargetRevision != prepared.targetRevision))
            { requestFresh(); return; }
            prepared.trace?.Mark("room.preparation_reused", new Newtonsoft.Json.Linq.JObject {
                ["captureSerial"] = prepared.capture,
                ["generation"] = generation,
                ["intent"] = prepared.decision?.intent,
                ["sceneEyesOpen"] = SceneEyesOpen,
                ["finalConfirmed"] = prepared.finalConfirmed,
                ["targetRevision"] = prepared.targetRevision,
                ["scope"] = "confirmed non-visual decision reused; visual decision requires fresh observation"
            });
            // A trace subscriber may synchronously start a new capture/turn.
            // The old result cannot be handed off after losing its source owner.
            if (IsFreshRoomPreparation(prepared) && generation == m_FormalResponseGeneration)
                accept(true, prepared.json, "closed-recording-preparation", prepared.text, prepared);
        }
        if (prepared.completed) Deliver();
        else
        {
            // Preparation can run in parallel with FastLoop/ActorIntent for the
            // full observed room-model duration. Once it becomes the critical
            // path, bound additional waiting; don't add its entire timeout.
            prepared.deadline = RoomPreparationBudget.JoinDeadline(prepared.deadline, Time.realtimeSinceStartup);
            prepared.continuation = Deliver;
            prepared.fallback = requestFresh;
            prepared.waitingFormalGeneration = generation;
        }
        return true;
    }
}
