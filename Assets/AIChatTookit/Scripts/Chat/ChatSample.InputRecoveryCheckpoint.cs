using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class ChatSample
{
    // A checkpoint is a completed read-only route to an UNPUBLISHED General leaf.
    // It never retains generated speech, tool callbacks, execution or audio leases.
    private sealed class InterruptedGeneralCheckpoint
    {
        public LLM Model;
        public SenseVoiceSpeechToText InputProvider;
        public UnityEngine.Object Avatar, VisionCapture;
        public ArdyRoomDialogueBridge RoomBridge;
        public string RoomActionId, RoomTaskId;
        public LLM.SendData AcceptedUser;
        public ActorGeneralCandidate Candidate;
        public string UserText, Prompt, Context;
        public long AcceptedRevision, InputSequence, OutputRevision;
        public int Generation, Epoch, InputRevision, UserTurn, SceneHandle, ActiveSceneHandle, SceneEpoch, SceneActionEpoch,
            RoomRevocation, Serial, SuspendedActorSerial, SuspendedGeneration, SuspendedSceneEpoch, SuspendedActionEpoch, Capture;
        public long SuspendedOutputRevision;
        public float CreatedAt;
        public bool RecordHistory, Sealed;
    }
    private InterruptedGeneralCheckpoint m_InterruptedGeneralCheckpoint;
    private ActorGeneralCandidate m_ResumedGeneralCandidate;
    private string m_ResumedGeneralBaseContext;
    private float m_ResumedGeneralOrigin;

    private bool GeneralCheckpointIdentityCurrent(InterruptedGeneralCheckpoint value) => value != null &&
        value.Model != null && value.InputProvider != null &&
        ReferenceEquals(value.Model, m_ChatSettings?.m_ChatModel) &&
        ReferenceEquals(value.InputProvider, m_ChatSettings?.m_SpeechToText) &&
        ReferenceEquals(value.AcceptedUser, m_CurrentInputHistoryUser) &&
        ReferenceEquals(value.Model, m_CurrentInputHistoryModel) &&
        value.Model.m_DataList.Contains(value.AcceptedUser) &&
        ReferenceEquals(value.AcceptedUser, value.Model.m_DataList.LastOrDefault(x => x?.role == "user")) &&
        value.AcceptedRevision == m_AcceptedRecordingInputRevision &&
        value.InputSequence == m_ActorInputSequence && value.UserTurn == m_ActionConstraints.CurrentUserTurn &&
        value.UserText == m_LastUserMsg && value.Epoch == m_WorkEpoch &&
        value.SceneHandle == gameObject.scene.handle &&
        value.ActiveSceneHandle == SceneManager.GetActiveScene().handle &&
        value.RoomRevocation == m_RoomActorProposalRevocation &&
        ReferenceEquals(value.Avatar, m_SceneVisionAvatar) && ReferenceEquals(value.VisionCapture, m_SceneVisionCapture) &&
        ReferenceEquals(value.RoomBridge, GetComponent<ArdyRoomDialogueBridge>()) &&
        value.RoomActionId == (value.RoomBridge?.ActionId ?? "") &&
        value.RoomTaskId == (value.RoomBridge?.ActiveTaskId ?? "") &&
        !m_SceneVisionApplicationPaused && isActiveAndEnabled &&
        m_EnableFastTaskLoop && m_EnableActorIntentCompiler;

    private void CaptureInterruptedGeneralCheckpoint()
    {
        m_InterruptedGeneralCheckpoint = null;
        var value = m_ActorGeneralCandidate;
        var provider = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        // Only the private General transport itself is reusable as a stage.
        // Actor compilation already begun, published speech or submitted work
        // requires the ordinary recovery with its current execution evidence.
        if (provider == null || !ActorGeneralCurrent(value) || value.Consumed || value.AwaitingPublication ||
            value.CompilingActor || !m_FormalResponseInFlight || m_AgentCurrentRoundIsTick ||
            m_RealFirstAudioStarted || IsAISpeaking || IsVoiceOutputPlaying ||
            m_AssistantHeardText.Length > 0 || m_PendingChunks.Count > 0 || m_PendingClips.Count > 0 ||
            HasActiveActorPlan || m_SceneActionActive || HasCurrentRecordingDecisionRoute() ||
            m_SingingRequestSubmittedSinceUserTurn || m_AcceptedRecordingInputRevision <= 0 ||
            m_CurrentInputHistoryUser == null ||
            (m_RoomTaskGeneration == value.Generation &&
                (m_RoomTaskIntent != "none" || !string.IsNullOrEmpty(m_RoomTaskActionId) ||
                    m_RoomTaskDispatch != "not-submitted")))
        {
            m_ResponseTrace?.Mark("input.recovery_checkpoint_not_captured", new JObject {
                ["reason"] = "no-unpublished-no-effect-general-leaf", ["candidateCurrent"] = ActorGeneralCurrent(value),
                ["candidateConsumed"] = value?.Consumed, ["compilingActor"] = value?.CompilingActor,
                ["responseInFlight"] = m_FormalResponseInFlight, ["publicAudioStarted"] = m_RealFirstAudioStarted,
                ["planActive"] = HasActiveActorPlan, ["sceneActionActive"] = m_SceneActionActive,
                ["recordingRoute"] = HasCurrentRecordingDecisionRoute(), ["acceptedInputRevision"] = m_AcceptedRecordingInputRevision });
            return;
        }
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var checkpoint = new InterruptedGeneralCheckpoint {
            Model = value.Model, InputProvider = provider, AcceptedUser = m_CurrentInputHistoryUser,
            Avatar = m_SceneVisionAvatar, VisionCapture = m_SceneVisionCapture, RoomBridge = bridge,
            RoomActionId = bridge?.ActionId ?? "", RoomTaskId = bridge?.ActiveTaskId ?? "",
            Candidate = value, UserText = m_LastUserMsg, Prompt = value.Prompt, Context = ReferenceEquals(value, m_ResumedGeneralCandidate) ? m_ResumedGeneralBaseContext : value.Context,
            AcceptedRevision = m_AcceptedRecordingInputRevision, InputSequence = m_ActorInputSequence,
            OutputRevision = m_FastLoop.OutputRevision, Generation = value.Generation, Epoch = m_WorkEpoch,
            InputRevision = m_InputAudioRevision, UserTurn = m_ActionConstraints.CurrentUserTurn,
            SceneHandle = gameObject.scene.handle, ActiveSceneHandle = SceneManager.GetActiveScene().handle, SceneEpoch = m_SceneVisionEpoch,
            SceneActionEpoch = m_SceneActionEpoch, RoomRevocation = m_RoomActorProposalRevocation,
            Serial = m_ActorGeneralSerial, CreatedAt = ReferenceEquals(value, m_ResumedGeneralCandidate) ? m_ResumedGeneralOrigin : Time.realtimeSinceStartup,
            RecordHistory = value.RecordHistory
        };
        if (!GeneralCheckpointIdentityCurrent(checkpoint)) return;
        m_InterruptedGeneralCheckpoint = checkpoint;
        m_ResponseTrace?.Mark("input.recovery_checkpoint_captured", new JObject {
            ["stage"] = "private-general", ["generation"] = value.Generation,
            ["acceptedInputRevision"] = checkpoint.AcceptedRevision,
            ["contextCharacters"] = checkpoint.Context?.Length ?? 0,
            ["scope"] = "Read-only route progress only; no old speech, source approval or executable lease retained." });
    }

    private void SealInterruptedGeneralCheckpoint()
    {
        var value = m_InterruptedGeneralCheckpoint;
        if (value == null) return;
        // Exactly the expected local VAD cancellation may advance these owners.
        // InvalidateFormalResponse calls CancelDialogueMotion (CancelSceneAction)
        // and ResetSceneVision calls CancelSceneAction again: action epoch +2.
        // The earlier Notify cancellation happened BEFORE the checkpoint capture.
        // A trace/cancel callback replacing the owner cannot be blessed by sealing.
        if (!GeneralCheckpointIdentityCurrent(value) || !m_UserSpeechActiveForAutonomy ||
            m_InputAudioRevision != value.InputRevision + 1 ||
            m_FormalResponseGeneration != value.Generation + 1 ||
            m_SceneVisionEpoch != value.SceneEpoch + 1 || m_SceneActionEpoch != value.SceneActionEpoch + 2 ||
            m_FastLoop.OutputRevision != value.OutputRevision + 1 ||
            m_ActorGeneralSerial != value.Serial || !ReferenceEquals(value.Candidate, m_ActorGeneralCandidate) ||
            value.Candidate.Consumed || value.InputProvider.InputCaptureSessionSerial <= 0)
        {
            m_ResponseTrace?.Mark("input.recovery_checkpoint_seal_rejected", new JObject {
                ["reason"] = "interruption-owner-not-exact", ["identityCurrent"] = GeneralCheckpointIdentityCurrent(value),
                ["userSpeechActive"] = m_UserSpeechActiveForAutonomy,
                ["inputRevisionDelta"] = m_InputAudioRevision - value.InputRevision,
                ["generationDelta"] = m_FormalResponseGeneration - value.Generation,
                ["sceneEpochDelta"] = m_SceneVisionEpoch - value.SceneEpoch,
                ["sceneActionEpochDelta"] = m_SceneActionEpoch - value.SceneActionEpoch,
                ["outputRevisionDelta"] = m_FastLoop.OutputRevision - value.OutputRevision,
                ["candidateSerialUnchanged"] = m_ActorGeneralSerial == value.Serial,
                ["sameCandidate"] = ReferenceEquals(value.Candidate, m_ActorGeneralCandidate),
                ["candidateConsumed"] = value.Candidate.Consumed,
                ["capture"] = value.InputProvider.InputCaptureSessionSerial });
            if (ReferenceEquals(value, m_InterruptedGeneralCheckpoint)) ClearInterruptedGeneralCheckpoint("interruption-owner-not-exact");
            return;
        }
        value.SuspendedActorSerial = m_ActorIntentRequestSerial;
        value.SuspendedGeneration = m_FormalResponseGeneration;
        value.SuspendedSceneEpoch = m_SceneVisionEpoch;
        value.SuspendedActionEpoch = m_SceneActionEpoch;
        value.SuspendedOutputRevision = m_FastLoop.OutputRevision;
        value.Capture = value.InputProvider.InputCaptureSessionSerial;
        value.Sealed = true;
        m_ResponseTrace?.Mark("input.recovery_checkpoint_sealed", new JObject {
            ["stage"] = "private-general", ["capture"] = value.Capture,
            ["acceptedInputRevision"] = value.AcceptedRevision, ["executionAuthorized"] = false });
    }

    private void ClearInterruptedGeneralCheckpoint(string reason)
    {
        var value = m_InterruptedGeneralCheckpoint;
        m_InterruptedGeneralCheckpoint = null;
        m_ResumedGeneralCandidate = null; m_ResumedGeneralBaseContext = null;
        if (value != null) m_ResponseTrace?.Mark("input.recovery_checkpoint_discarded", new JObject {
            ["stage"] = "private-general", ["reason"] = reason,
            ["acceptedInputRevision"] = value.AcceptedRevision });
    }

    private bool InterruptedGeneralCheckpointCurrent(InterruptedGeneralCheckpoint value, int generation) =>
        GeneralCheckpointIdentityCurrent(value) && value.Sealed && !m_UserSpeechActiveForAutonomy &&
        generation == m_FormalResponseGeneration && generation == value.SuspendedGeneration + 1 &&
        m_SceneVisionEpoch == value.SuspendedSceneEpoch && m_SceneActionEpoch == value.SuspendedActionEpoch &&
        m_FastLoop.OutputRevision == value.SuspendedOutputRevision &&
        m_InputAudioRevision == value.InputRevision + 1 && m_ActorGeneralSerial == value.Serial &&
        m_ActorIntentRequestSerial == value.SuspendedActorSerial &&
        ReferenceEquals(value.Candidate, m_ActorGeneralCandidate) && !value.Candidate.Consumed &&
        Time.realtimeSinceStartup - value.CreatedAt >= 0f && Time.realtimeSinceStartup - value.CreatedAt <= 30f &&
        !m_RealFirstAudioStarted && !IsAISpeaking && !IsVoiceOutputPlaying &&
        !HasActiveActorPlan && !m_SceneActionActive &&
        value.InputProvider.InputCaptureSessionSerial == value.Capture &&
        value.InputProvider.LastFinalCaptureSerial == value.Capture &&
        value.InputProvider.LastFinalTranscriptSource == "rejected" &&
        ExplicitRejectedInput(value.InputProvider.LastFinalRejectionReason);

    private static bool ExplicitRejectedInput(string reason) => reason == "self-speech" ||
        reason == "hallucination-rejected" || reason == "vad-insufficient-speech" || reason == "vad-no-speech";

    private bool TryResumeInterruptedGeneralCheckpoint(string prompt, string imageUrl, int generation)
    {
        var value = m_InterruptedGeneralCheckpoint;
        if (value == null) return false;
        m_InterruptedGeneralCheckpoint = null; // claim once before diagnostic/provider reentry
        if (!InterruptedGeneralCheckpointCurrent(value, generation))
        {
            m_ResponseTrace?.Mark("input.recovery_checkpoint_rejected", new JObject {
                ["stage"] = "private-general", ["generation"] = generation,
                ["reason"] = "stale-or-not-explicit-rejection", ["executionAuthorized"] = false });
            return false;
        }
        var facts = BuildCompactInputRecoveryFacts(null);
        facts["retainedStage"] = "private-general";
        facts["sourceContextIsHistorical"] = true;
        string context = "[中断前已完成的只读路由准备；不是当前视觉或执行许可]\n" +
            (value.Context ?? "") + "\n[当前恢复边界]\n" + facts.ToString(Newtonsoft.Json.Formatting.None) +
            "\n保留原用户完整目标及已有真实结果。此前计划/图像只属于原准备；新图像与当前执行器事实优先。" +
            "从普通草稿阶段重新判断未完成目标；不要续播旧草稿，不把旧提案当已执行。草稿仍须当前Actor审核。" +
            BuildFormalObservationContext("");
        m_ResponseTrace?.Mark("input.recovery_routed", new JObject {
            ["generation"] = generation, ["sameAcceptedUser"] = true, ["path"] = "private-general-checkpoint",
            ["freshImage"] = !string.IsNullOrEmpty(imageUrl), ["contextCharacters"] = context.Length,
            ["skippedStages"] = new JArray("Fast", "initial-Actor", "Role", "Room"),
            ["publicSpeechReused"] = false, ["executionAuthorized"] = false });
        if (!InterruptedGeneralCheckpointCurrent(value, generation)) return true;
        // This reissues a private candidate, never the old callback, audio or
        // action. The normal General -> Actor -> typed execution path remains.
        bool handled = TryDispatchActorGeneralDraft(value.Prompt, imageUrl, generation,
            value.RecordHistory, context, true);
        var next = m_ActorGeneralCandidate;
        if (handled && next != null && next.Generation == generation && generation == m_FormalResponseGeneration &&
            ReferenceEquals(next.Model, value.Model) && next.Context == context)
        {
            // Repeated noise retains one base snapshot and the FIRST expiry,
            // rather than nesting prior recovery facts or refreshing its lifetime.
            m_ResumedGeneralCandidate = next;
            m_ResumedGeneralBaseContext = value.Context;
            m_ResumedGeneralOrigin = value.CreatedAt;
        }
        return handled;
    }

    private JObject BuildCompactInputRecoveryFacts(FastLoopTurnContext turn) => new JObject {
        ["sameUserEvent"] = true, ["eventSequence"] = m_ActorInputSequence,
        ["acceptedInputRevision"] = m_AcceptedRecordingInputRevision,
        ["userText"] = m_LastUserMsg ?? "", ["heardText"] = m_InterruptedHeardText ?? "",
        ["completedPlanCheckpoint"] = m_RecoveredPlanCheckpoint?.DeepClone(),
        ["scope"] = "The interrupted candidate produced no accepted new user input. This is not proof the user was silent. " +
            "Keep the whole original goal, verified completed results and actually heard speech. " +
            "Current executor and image evidence govern all new effects. Old prepared text is neither spoken history nor permission to execute. " +
            "Recovery does not append another perception frame or turn context inside the current facts."
    };
}
