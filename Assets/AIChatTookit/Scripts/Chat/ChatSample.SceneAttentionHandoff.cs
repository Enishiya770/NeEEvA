using NeEEvA.Motion;
using UnityEngine;

public partial class ChatSample
{
    private string m_CompletedSceneAttentionOwner;

    private void RememberCompletedSceneAttentionOwner(SceneActionFact fact)
    {
        if (fact == null || string.IsNullOrEmpty(fact.actionId)) return;
        if ((fact.phase == "observed" || fact.phase == "performed" || fact.phase == "already-satisfied") &&
            m_SceneAttention != null && m_SceneAttention.IsSettled && m_SceneAttention.OwnerId == fact.actionId)
            m_CompletedSceneAttentionOwner = fact.actionId;
        else if (fact.actionId == m_CompletedSceneAttentionOwner &&
            (fact.phase == "cancelled" || fact.phase == "rejected")) m_CompletedSceneAttentionOwner = null;
    }

    // Runs only after the next object action passes its existing input, current-view,
    // binding, capability and body-priority checks, but before asynchronous matching.
    private bool TryHandOffSceneViewToObjectPreparation(SceneActionFact next, int generation)
    {
        if (generation != m_FormalResponseGeneration || m_SceneActionActive ||
            m_UserSpeechActiveForAutonomy || !SceneEyesOpen || next == null ||
            string.IsNullOrEmpty(next.actionId) || m_SceneAttention == null || !m_SceneAttention.IsSettled ||
            (next.intent != "look-at" && next.intent != "inspect-object" && next.intent != "set-object-state" &&
             next.intent != "approach-object" && next.intent != "reposition-view")) return false;
        string previous = m_SceneAttention.OwnerId;
        var prior = m_SceneActionFact;
        // A rejected proposal replaces lastAction but never acquired the held
        // view. Keep the completed owner's provenance for a corrected proposal.
        bool completedSceneOwner = previous == m_CompletedSceneAttentionOwner ||
            prior != null && prior.actionId == previous &&
            (prior.phase == "observed" || prior.phase == "performed" || prior.phase == "already-satisfied");
        var head = m_FastLoop.HeadTask;
        bool fastHeadOwner = head?.Active == true && FastHeadOwner(head.Id, head.Revision) == previous;
        var avatar = m_SceneVisionCapture != null ? m_SceneVisionCapture.Target : null;
        var eyes = avatar != null ? avatar.GetComponent<NevaEyeTracking>() : null;
        if ((!completedSceneOwner && !fastHeadOwner) || avatar == null || eyes == null ||
            m_SceneAttention.Target != avatar || eyes.AttentionOwnerId != previous ||
            !m_SceneAttention.TryTransferSettledView(previous, next.actionId, out _)) return false;
        // The old persistent verifier must not renew or cancel the new lease. Its
        // ordinary scoped Release(previous) cannot affect the transferred owner.
        if (m_PersistentSceneAttention?.actionId == previous)
            EndPersistentSceneAttention(m_PersistentSceneAttention, "handed-to-object-preparation");
        Debug.Log("[Vision/AttentionHandoff] previous=" + previous + " next=" + next.actionId +
            " generation=" + generation + " holdSeconds=30 scope=held-view-only-not-object-confirmation", this);
        return true;
    }
}
