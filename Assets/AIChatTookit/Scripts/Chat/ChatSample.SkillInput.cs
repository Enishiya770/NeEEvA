using System;
using UnityEngine;

public partial class ChatSample
{
    // Only a final capture can attach acoustic relevance to a user turn. Display text,
    // protocol explanations and observations from an earlier recording are not topics.
    private int m_SkillObservationInputRevision = -1;
    private string m_SkillObservationTranscript;
    private bool m_SkillObservationSinging;
    private bool m_RoutingSingingObservation;

    private void StageSkillInputObservation(string transcript, bool singingObserved)
    {
        m_SkillObservationInputRevision = m_InputAudioRevision;
        m_SkillObservationTranscript = transcript ?? "";
        m_SkillObservationSinging = singingObserved;
    }

    private bool ConsumeSkillInputObservation(string transcript, bool fromAudio)
    {
        bool matches = fromAudio && m_SkillObservationInputRevision == m_InputAudioRevision &&
            string.Equals(m_SkillObservationTranscript, transcript ?? "", StringComparison.Ordinal);
        bool observed = matches && m_SkillObservationSinging;
        m_SkillObservationInputRevision = -1;
        m_SkillObservationTranscript = null;
        m_SkillObservationSinging = false;
        return observed;
    }

    private void PrepareSkillsForAcceptedUserInput(string utterance, bool singingObserved)
    {
        m_RoutingSingingObservation = singingObserved;
        try { PrepareActiveSkillsForRound(utterance ?? "", "user-spoke"); }
        finally { m_RoutingSingingObservation = false; }
        if (m_LogAgentLoop)
            Debug.Log($"[Skill/Input] source=user-transcript acoustic_singing_observation={singingObserved} " +
                $"singing_loaded={GetSkillRouteState("singing").ActiveThisRound}; observation is not an action request");
    }
}
