using System;
using System.Collections;
using UnityEngine;

public partial class ChatSample
{
    // This owns only the otherwise idle preparation transport while scene capture
    // is pending. It cannot authorize any movement other than stopping the exact task.
    private sealed class SceneVisionStopPreparation
    {
        public int generation, workEpoch, userTurn, operation;
        public string text, taskId, actionId, dispatch, reviewInput, reviewJson;
        public int inputRevision;
        public UnityEngine.Object avatar, environment;
        public float deadline;
        public bool completed, ownsTransport, following;
        public LLM provider;
        public ArdyRoomDialogueBridge bridge;
    }

    private SceneVisionStopPreparation m_SceneVisionStopPreparation;
    private int m_SceneVisionStopSequence;

    private void BeginSceneVisionStopPreparation(int generation)
    {
        if (m_SceneVisionStopPreparation != null && m_SceneVisionStopPreparation.generation == generation &&
            SceneVisionStopScopeIsCurrent(m_SceneVisionStopPreparation)) return;
        CancelSceneVisionStopPreparation();
        CancelSequenceContinuation("new-stop-preparation");
        // Even an obsolete closed-speech object may still own the provider's
        // preparation channel. Its own cancellation path must release it first.
        if (!SceneEyesOpen || !isActiveAndEnabled || generation != m_FormalResponseGeneration ||
            m_AgentCurrentRoundIsTick || m_UserSpeechActiveForAutonomy || !HasRoomTaskPlanner ||
            m_RoomSpeechPreparation != null) return;
        var provider = m_ChatSettings.m_ChatModel;
        if (!provider.isActiveAndEnabled || !provider.SupportsRoomPreparationMessages) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        string text = m_ActionConstraints.CurrentUserText;
        if (bridge == null || !bridge.IsConnected || (!bridge.ReservesBody && !bridge.IsFollowing) ||
            string.IsNullOrWhiteSpace(bridge.ActiveTaskId) || string.IsNullOrWhiteSpace(text)) return;
        var prepared = new SceneVisionStopPreparation {
            generation = generation, workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn,
            operation = bridge.Controller.OperationRevision, taskId = bridge.ActiveTaskId, following = bridge.IsFollowing,
            inputRevision = m_InputAudioRevision, avatar = bridge.avatar, environment = bridge.environmentRoot,
            provider = provider, bridge = bridge, text = text,
            deadline = Time.realtimeSinceStartup + 2.75f, ownsTransport = true
        };
        m_SceneVisionStopPreparation = prepared;
        try
        {
            // No image, no scene-capture dependency, no dialogue history mutation.
            string input = RoomControlReviewProtocol.BodyInput(text, prepared.taskId, prepared.operation,
                bridge.Phase, bridge.IsFollowing, false).ToString(Newtonsoft.Json.Formatting.None);
            prepared.reviewInput = input;
            provider.PostRoomPreparationMessage(input, (ok, json, status) => {
                if (!ReferenceEquals(m_SceneVisionStopPreparation, prepared) || prepared.completed) return;
                CheckSceneVisionStopDeadline(prepared, Time.realtimeSinceStartup);
                if (!ReferenceEquals(m_SceneVisionStopPreparation, prepared) || prepared.completed) return;
                prepared.completed = true; prepared.ownsTransport = false;
                if (ok && RoomControlReviewProtocol.TryBodyDecision(json, prepared.text, false, out _, out _))
                    prepared.reviewJson = json;
                if (!SceneVisionStopScopeIsCurrent(prepared) ||
                    prepared.bridge.ActiveTaskId != prepared.taskId ||
                    !prepared.following && prepared.bridge.Controller.OperationRevision != prepared.operation ||
                    !ok || !RoomControlReviewProtocol.TryBodyDecision(json, prepared.text, false, out var decision, out _) || decision.intent != "stop-moving") return;
                string id = m_MotionIdentityPrefix + ":vision-stop:" + generation + ":" + (++m_SceneVisionStopSequence);
                bool stopped = prepared.bridge.StopForTaskIfCurrent(id, prepared.taskId, prepared.operation, out string failure, prepared.following);
                if (stopped) { prepared.actionId = id; prepared.dispatch = "stop-submitted"; }
                Debug.Log($"[Room/VisionQuickStop] generation={generation} task={prepared.taskId} operation={prepared.operation} committed={stopped}", this);
            });
        }
        catch (Exception error)
        {
            CancelSceneVisionStopPreparation();
            Debug.LogWarning("[Room/VisionQuickStop] preparation unavailable: " + error.Message, this);
            return;
        }
        if (ReferenceEquals(m_SceneVisionStopPreparation, prepared) && !prepared.completed)
            StartCoroutine(WatchSceneVisionStopPreparation(prepared));
    }

    private bool SceneVisionStopScopeIsCurrent(SceneVisionStopPreparation prepared) =>
        prepared != null && isActiveAndEnabled && prepared.generation == m_FormalResponseGeneration &&
        prepared.workEpoch == m_WorkEpoch && prepared.userTurn == m_ActionConstraints.CurrentUserTurn &&
        !m_AgentCurrentRoundIsTick && !m_UserSpeechActiveForAutonomy &&
        prepared.provider != null && prepared.provider.isActiveAndEnabled && m_ChatSettings?.m_ChatModel == prepared.provider &&
        prepared.bridge != null && prepared.bridge.IsConnected && GetComponent<ArdyRoomDialogueBridge>() == prepared.bridge;

    private bool TryReusePreparedBodyReview(SceneVisionStopPreparation prepared, string currentInput, out string json)
    {
        json = null;
        if (prepared == null || !prepared.completed || !SceneVisionStopScopeIsCurrent(prepared) ||
            prepared.inputRevision != m_InputAudioRevision || prepared.avatar != prepared.bridge.avatar ||
            prepared.environment != prepared.bridge.environmentRoot ||
            !RoomControlReviewProtocol.SameBodyEvidence(prepared.reviewInput, currentInput, prepared.reviewJson)) return false;
        json = prepared.reviewJson;
        return true;
    }

    private IEnumerator WatchSceneVisionStopPreparation(SceneVisionStopPreparation prepared)
    {
        while (ReferenceEquals(m_SceneVisionStopPreparation, prepared) && !prepared.completed)
        {
            yield return null;
            CheckSceneVisionStopDeadline(prepared, Time.realtimeSinceStartup);
        }
    }

    private void CheckSceneVisionStopDeadline(SceneVisionStopPreparation prepared, float now)
    {
        if (!ReferenceEquals(m_SceneVisionStopPreparation, prepared) || prepared.completed) return;
        if (!SceneVisionStopScopeIsCurrent(prepared) || now >= prepared.deadline ||
            (m_RoomSpeechPreparation != null && m_RoomSpeechPreparation.provider == prepared.provider))
            CancelSceneVisionStopPreparation();
    }

    private void CancelSceneVisionStopPreparation()
    {
        var prepared = m_SceneVisionStopPreparation;
        m_SceneVisionStopPreparation = null;
        if (prepared == null) return;
        bool cancelOwnedTransport = prepared.ownsTransport &&
            (m_RoomSpeechPreparation == null || m_RoomSpeechPreparation.provider != prepared.provider);
        prepared.completed = true; prepared.ownsTransport = false;
        // If a closed-speech request already took over, cancelling this provider
        // would abort its newer request. The detached callback is fenced above.
        if (cancelOwnedTransport && prepared.provider != null) prepared.provider.CancelRoomPreparationMessage();
    }

    private bool TryGetSceneVisionPreparedStop(int generation, out string actionId, out string dispatch)
    {
        actionId = dispatch = "";
        var prepared = m_SceneVisionStopPreparation;
        if (prepared == null || generation != prepared.generation || !prepared.completed ||
            string.IsNullOrEmpty(prepared.actionId) || !SceneVisionStopScopeIsCurrent(prepared) ||
            prepared.bridge.ActionId != prepared.actionId) return false;
        actionId = prepared.actionId; dispatch = prepared.dispatch;
        return true;
    }
}
