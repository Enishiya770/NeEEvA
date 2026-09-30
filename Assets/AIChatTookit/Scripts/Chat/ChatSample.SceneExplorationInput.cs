using UnityEngine;

public partial class ChatSample
{
    // Manual PTT deliberately does not enter the realtime VAD state machine.
    // Keep its input ownership separate, including a delayed legacy WebGL clip.
    private bool m_SceneExplorationManualInputActive;
    private int m_SceneExplorationManualInputOwner;
    private int m_LegacyManualInputOwner = -1;
    internal int ManualCaptureInputOwner => m_SceneExplorationManualInputOwner;

    private bool SceneExplorationInputPending =>
        m_UserSpeechActiveForAutonomy || m_SceneExplorationManualInputActive ||
        m_FinalAsrRequestsInFlight > 0 ||
        (m_RTSpeechHandler != null && m_RTSpeechHandler.ChatOwner == this &&
            (m_RTSpeechHandler.IsManualCaptureActive || m_RTSpeechHandler.IsRecording));

    private void BeginSceneExplorationManualInput()
    {
        ++m_SceneExplorationManualInputOwner;
        m_SceneExplorationManualInputActive = true;
        SuspendSceneExplorationStep("manual-capture-started");
        CancelAutonomyIntentProbe();
    }

    // Call after the clip handoff has registered final ASR, or after a discarded
    // capture. A late clip/cancel from an older capture cannot release a new one.
    public void NotifyManualCaptureEnded(int inputOwner)
    {
        if (inputOwner < 0 || inputOwner != m_SceneExplorationManualInputOwner) return;
        bool wasActive = m_SceneExplorationManualInputActive;
        m_SceneExplorationManualInputActive = false;
        if (wasActive && m_SceneExploration.active && isActiveAndEnabled)
            ScheduleSceneExplorationStep(2f, "scene-exploration-input-ended");
    }

    private void SuspendSceneExplorationStep(string reason)
    {
        bool ownedStep = m_ExplorationStepGeneration >= 0 &&
            m_ExplorationStepGeneration == m_FormalResponseGeneration;
        m_ExplorationStepGeneration = m_ExplorationStepRevision = -1;
        if (!ownedStep) return;
        ++m_FormalResponseGeneration;
        m_FormalResponseInFlight = false;
        m_AgentRoundInFlight = false;
        m_AgentCurrentRoundIsTick = false;
        m_QuietAutonomyRound = false;
        m_StreamComplete = true;
        m_TTSSenderDone = true;
        m_PendingSceneFormalCaptureGeneration = -1;
        ClearStagedFormalObservation();
        if (m_SceneExploration.active)
        {
            m_SceneExploration.phase = reason == "manual-capture-started" ? "waiting-for-user-input" : "waiting-for-perception";
            m_SceneExploration.reason = reason;
        }
        // The existing input/pause cancellation path owns any submitted body action.
        // This only revokes a planner/capture callback; the parent goal survives.
    }
}
