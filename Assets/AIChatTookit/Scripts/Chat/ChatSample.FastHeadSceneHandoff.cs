using NeEEvA.Motion;

public partial class ChatSample
{
    private bool CanRetainFastPoseForObjectPreparation(string owner)
    {
        var fact = m_SceneActionFact;
        if (!m_SceneActionActive || m_PendingSceneFormalCaptureGeneration != m_FormalResponseGeneration ||
            m_UserSpeechActiveForAutonomy || fact == null || fact.phase != "preparing" ||
            string.IsNullOrEmpty(fact.actionId) || fact.actionId == owner ||
            m_SceneAttention == null || m_SceneAttention.OwnerId != owner || !m_SceneAttention.IsSettled)
            return false;
        // These operations match the current image before moving gaze. A body
        // turn, locomotion writer or arbitrary scene action never borrows it.
        return fact.intent == "look-at" || fact.intent == "inspect-object" ||
            fact.intent == "set-object-state" || fact.intent == "approach-object" || fact.intent == "reposition-view";
    }

    private bool HasFastHeadSceneSuccessor(string owner)
    {
        var fact = m_SceneActionFact;
        if (fact == null || string.IsNullOrEmpty(fact.actionId) || fact.actionId == owner ||
            m_SceneAttention == null || m_SceneAttention.LastOwnerId != owner ||
            m_SceneAttention.OwnerId != fact.actionId || !m_SceneAttention.IsBusy || m_SceneAttention.IsReturning)
            return false;
        var avatar = m_SceneVisionCapture != null ? m_SceneVisionCapture.Target : null;
        var eyes = avatar != null ? avatar.GetComponent<NevaEyeTracking>() : null;
        return eyes != null && eyes.isActiveAndEnabled && eyes.IsInitialized &&
            m_SceneAttention.Target == avatar && eyes.AttentionOwnerId == fact.actionId;
    }
}
