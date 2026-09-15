using System;
using System.Collections;
using NeEEvA.Motion;
using NeEEvA.Perception;
using UnityEngine;

public partial class ChatSample
{
    // Stream step zero allows 20 s for cold Qwen features; add a bounded transport margin.
    private const float RoomMotionReadinessLimitSeconds = 22f;
    private int m_RoomMotionReadinessRevision;
    public bool IsWaitingForRoomMotionReadiness { get; private set; }
    public string RoomMotionReadinessStatus { get; private set; } = "idle";

    private void DispatchRoomMotionReplyWhenReady(ArdyRoomDialogueBridge bridge,
        int generation, int workEpoch, int userTurn, Action dispatch)
    {
        if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
            userTurn != m_ActionConstraints.CurrentUserTurn || m_UserSpeechActiveForAutonomy || !isActiveAndEnabled) return;
        int revision = ++m_RoomMotionReadinessRevision;
        IsWaitingForRoomMotionReadiness = false;
        bool submitted = m_RoomTaskIntent == "follow" && m_RoomTaskDispatch == "follow-submitted" ||
            m_RoomTaskIntent == "approach" && m_RoomTaskDispatch == "attempt-submitted" ||
            m_RoomTaskIntent == "approach-object" && m_RoomTaskDispatch == "object-attempt-submitted" ||
            m_RoomTaskIntent == "reposition-view" && m_RoomTaskDispatch == "view-position-attempt-submitted";
        if (!submitted || m_ChatSettings?.m_ChatModel?.RequiresRoomMotionReadinessBeforeSpeech != true ||
            bridge == null || string.IsNullOrEmpty(m_RoomTaskActionId))
        { RoomMotionReadinessStatus = "not-required"; dispatch?.Invoke(); return; }
        string action = m_RoomTaskActionId;
        var avatar = bridge.avatar; var environment = bridge.environmentRoot;
        var userAnchor = bridge.userHead; var provider = m_ChatSettings.m_ChatModel;
        var controller = bridge.Controller; var navigation = bridge.Navigation;
        int operation = 0;
        bool assignedOperation = false;
        bool Current() => revision == m_RoomMotionReadinessRevision && generation == m_FormalResponseGeneration &&
            workEpoch == m_WorkEpoch && userTurn == m_ActionConstraints.CurrentUserTurn &&
            !m_UserSpeechActiveForAutonomy && isActiveAndEnabled && bridge != null && bridge.IsConnected &&
            bridge.ActionId == action && bridge.chat == this && bridge.Controller == controller && bridge.avatar == avatar &&
            bridge.Navigation == navigation && controller.Avatar == avatar && controller.Navigation == navigation &&
            navigation.EnvironmentRoot == environment && bridge.environmentRoot == environment &&
            bridge.userHead == userAnchor && m_ChatSettings?.m_ChatModel == provider;
        if (!Current())
        {
            RoomMotionReadinessStatus = "owner-invalidated";
            ReleaseUnsentRoomMotionReply(revision, generation, RoomMotionReadinessStatus);
            return;
        }
        string Ready()
        {
            // A terminal bridge result needs a truthful reply even if no controller was ever assigned.
            switch (bridge.Phase)
            {
                case "failed": case "cancelled": case "stopped": case "unavailable": case "unreachable":
                case "blocked": case "goal-not-reached": case "follow-paused": case "arrived": return "terminal-" + bridge.Phase;
                case "follow-waiting": return "waiting-near-user";
            }
            if (assignedOperation && controller.OperationRevision != operation) return "operation-replaced";
            if (!bridge.TryGetSubmittedMotionOperation(action, out var adopted, out int actualOperation)) return null;
            if (adopted != controller) return "operation-replaced";
            // The accepted action may initially be waiting for an earlier body handoff.
            // Adopt its real operation once it exists, not the preceding operation's token.
            if (!assignedOperation) { operation = actualOperation; assignedOperation = true; }
            else if (operation != actualOperation) return "operation-replaced";
            if (controller.ExecutionState == ArdyRoomExecutionState.Failed ||
                controller.ExecutionState == ArdyRoomExecutionState.Blocked ||
                controller.ExecutionState == ArdyRoomExecutionState.Cancelled) return "terminal-" + controller.ExecutionState;
            if (controller.OwnsActivePlayerPose && controller.Player != null &&
                (controller.Player.IsPlaying || controller.Player.CompletedNaturally || controller.Player.IsReturningToAnimation))
                return "actual-player-pose-ready";
            if (controller.StreamStepCount > 0 && !string.IsNullOrEmpty(controller.StreamSessionId))
                return "first-audited-stream-prefix-ready";
            if (controller.Stage == "stream-waiting") return "waiting-near-user";
            return null;
        }
        string immediate = Ready();
        if (immediate != null)
        {
            RoomMotionReadinessStatus = immediate;
            if (immediate != "operation-replaced" && Current()) dispatch?.Invoke();
            else ReleaseUnsentRoomMotionReply(revision, generation, immediate);
            return;
        }
        IsWaitingForRoomMotionReadiness = true; RoomMotionReadinessStatus = "waiting-first-motion-result";
        StartCoroutine(WaitForRoomMotionReadiness(revision, action, generation, Current, Ready, dispatch));
    }

    private IEnumerator WaitForRoomMotionReadiness(int revision, string action, int generation,
        Func<bool> current, Func<string> readiness, Action dispatch)
    {
        float started = Time.realtimeSinceStartup;
        Debug.Log($"[Room/MotionReadiness] generation={generation} action={action} state=waiting-first-result limit={RoomMotionReadinessLimitSeconds:F0}s", this);
        string outcome = null;
        bool dispatched = false;
        try
        {
            while (current())
            {
                outcome = readiness();
                if (outcome != null) break;
                if (Time.realtimeSinceStartup - started >= RoomMotionReadinessLimitSeconds)
                { outcome = "bounded-wait-expired-still-preparing"; break; }
                yield return null;
            }
            if (!current()) outcome = "owner-invalidated";
            if (revision != m_RoomMotionReadinessRevision) yield break;
            RoomMotionReadinessStatus = outcome ?? "owner-invalidated";
            Debug.Log($"[Room/MotionReadiness] generation={generation} action={action} state={RoomMotionReadinessStatus} elapsed={Time.realtimeSinceStartup-started:F2}s", this);
            if (current() && outcome != "operation-replaced")
            {
                // The character may have moved during the first-feature wait. Never
                // send the original room image as if it depicts this dispatch time.
                BindFormalSceneObservation(null, generation);
                if (SceneEyesOpen && m_SceneVisionCapture != null)
                {
                    bool captured = false; SceneVisionObservation result = null;
                    m_PendingSceneFormalCaptureGeneration = generation;
                    float captureDeadline = Time.realtimeSinceStartup + 4f;
                    RequestSceneObservation("motion-readiness-result", observation => {
                        if (!current()) return;
                        result = observation; captured = true;
                    });
                    while (current() && !captured && Time.realtimeSinceStartup < captureDeadline) yield return null;
                    if (!current()) yield break;
                    if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
                    BindFormalSceneObservation(IsFreshSceneObservation(result) ? result : null, generation);
                    Debug.Log($"[Room/MotionReadiness] generation={generation} state=reply-observation capture={GetFormalSceneObservation(generation)?.CaptureId ?? "none"}", this);
                }
                // A different operation can take control while GPU readback is pending.
                if (current() && readiness() == "operation-replaced")
                    RoomMotionReadinessStatus = outcome = "operation-replaced";
            }
            IsWaitingForRoomMotionReadiness = false;
            // No cancellation, retry, trajectory change or success claim is made by this gate.
            // Dispatch builds execution facts now, after the actual result/timeout, not at submission.
            if (current() && outcome != "operation-replaced") { dispatched = true; dispatch?.Invoke(); }
        }
        finally
        {
            if (revision == m_RoomMotionReadinessRevision)
            {
                IsWaitingForRoomMotionReadiness = false;
                if (!current()) RoomMotionReadinessStatus = "owner-invalidated";
                if (!dispatched) ReleaseUnsentRoomMotionReply(revision, generation, RoomMotionReadinessStatus);
            }
        }
    }

    private void ReleaseUnsentRoomMotionReply(int revision, int generation, string reason)
    {
        // A new user/request may already own these shared pipeline flags. This waiter
        // may release only its still-current, never-dispatched formal generation.
        if (revision != m_RoomMotionReadinessRevision || generation != m_FormalResponseGeneration) return;
        IsWaitingForRoomMotionReadiness = false;
        m_FormalResponseInFlight = false; m_AgentRoundInFlight = false;
        m_StreamComplete = true; m_TTSSenderDone = true;
        if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
        if (m_RoomSpeechGuardGeneration == generation) m_RoomSpeechGuardGeneration = -1;
        ++m_LatencyFillerGeneration; ++m_EouFillerGeneration; m_EouFillerScheduled = false;
        ClearStagedFormalObservation();
        // Do not call a general cancellation path: an Editor operation or another
        // body owner may have legitimately replaced the move while speech waited.
        Debug.Log($"[Room/MotionReadiness] generation={generation} state=unsent-pipeline-released reason={reason}", this);
    }

    private string RoomMotionReadinessReplyContext()
        => "\n[本轮移动起步调度] " + RoomMotionReadinessStatus +
            "；仅表示起步数据/预缓冲等待的实际结果，不证明已经走动、到达目标或满足动作含义。" +
            " bounded-wait-expired-still-preparing表示等待预算已用完，动作仍可能准备中；以当前Unity执行阶段为准，不能说动作已成功或被本调度取消。";
}
