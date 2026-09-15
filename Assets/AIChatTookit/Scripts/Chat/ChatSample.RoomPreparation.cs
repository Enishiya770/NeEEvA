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
        public RoomTaskProtocol.Decision decision;
        public Action continuation;
        public Action fallback;
        public int waitingFormalGeneration = -1;
    }
    private RoomSpeechPreparation m_RoomSpeechPreparation;
    private int m_RoomPreparationSequence;

    public void AcceptClosedSpeechEvidence(ClosedSpeechEvidence evidence)
    {
        if (!HasRoomTaskPlanner || !m_AutoSend || !enabled || !gameObject.activeInHierarchy || evidence == null ||
            m_ChatSettings.m_ChatModel.SupportsRoomPreparationMessages != true ||
            evidence.CaptureSessionSerial <= 0 || string.IsNullOrWhiteSpace(evidence.Text) || evidence.Text.Length > 160 ||
            evidence.NormalizedText != ClosedSpeechEvidence.Normalize(evidence.Text) ||
            evidence.StableForSeconds < .35f || Time.realtimeSinceStartup - evidence.ClosedAt > 1f) return;
        var speechProvider = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        if (speechProvider == null || speechProvider.InputCaptureSessionSerial != evidence.CaptureSessionSerial ||
            !m_ChatSettings.m_ChatModel.isActiveAndEnabled) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (!bridge.ReservesBody && !bridge.IsFollowing) return;
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
            deadline = Time.realtimeSinceStartup + 2.75f, provider = m_ChatSettings.m_ChatModel,
            speechProvider = speechProvider
        };
        m_RoomSpeechPreparation = prepared;
        Debug.Log($"[Room/Preparation] capture={prepared.capture} input={prepared.inputRevision} task={prepared.taskId} operation={prepared.operationRevision} state=submitted", this);
        m_ChatSettings.m_ChatModel.PostRoomPreparationMessage(BuildRoomDecisionInput(prepared.text, false, "", bridge), (ok, json, status) => {
            CheckRoomPreparationWait(prepared, Time.realtimeSinceStartup);
            if (!IsFreshRoomPreparation(prepared) || prepared.completed) return;
            prepared.completed = true;
            prepared.json = json;
            prepared.status = status;
            prepared.accepted = ok && RoomTaskProtocol.TryDecision(json, prepared.text, false, out prepared.decision);
            CommitPreparedRoomStop(prepared);
            var continuation = prepared.continuation; prepared.continuation = null; continuation?.Invoke();
        });
        StartCoroutine(WatchRoomPreparation(prepared));
    }

    private bool IsFreshRoomPreparation(RoomSpeechPreparation prepared) => prepared != null &&
        ReferenceEquals(prepared, m_RoomSpeechPreparation) && enabled && gameObject.activeInHierarchy &&
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
        if (fallback != null)
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
            !string.IsNullOrWhiteSpace(finalText) && ClosedSpeechEvidence.Normalize(finalText) == prepared.normalized;
        if (!confirmed)
        {
            CancelClosedRoomPreparation("final-rejected-or-revised");
            return;
        }
        prepared.finalConfirmed = true;
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
            // Preserve the fast confirmed stop, but do not let that preparation
            // replace the visual planner for other intents in an open-eye round.
            if (SceneEyesOpen && prepared.decision?.intent != "stop-moving")
            { requestFresh(); return; }
            if (!prepared.accepted || (prepared.decision.intent != "stop-moving" &&
                bridge.ReadCurrentTargetEvidence().currentTargetRevision != prepared.targetRevision))
            { requestFresh(); return; }
            accept(true, prepared.json, "closed-recording-preparation", prepared.text, prepared);
        }
        if (prepared.completed) Deliver();
        else
        {
            prepared.continuation = Deliver;
            prepared.fallback = requestFresh;
            prepared.waitingFormalGeneration = generation;
        }
        return true;
    }
}
