using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private PreparedPlaybackEvidence m_PreparedPlaybackEvidence;
    private AudioClip m_PreparedPlaybackEvidenceClip;
    private int m_PreparedPlaybackEvidenceGeneration;
    private JObject m_PreparedPlaybackReceipt;
    private void BeginPreparedPlaybackEvidence(AudioClip clip,int generation)
    {
        if(m_PreparedPlaybackEvidence!=null && m_PreparedPlaybackReceipt==null) {
            m_PreparedPlaybackEvidence.Revoke("playback-replaced");
            FinishPreparedPlaybackEvidence(m_PreparedPlaybackEvidenceClip,m_PreparedPlaybackEvidenceGeneration);
        }
        m_PreparedPlaybackEvidenceClip=clip;m_PreparedPlaybackEvidenceGeneration=generation;m_PreparedPlaybackReceipt=null;
        m_PreparedPlaybackEvidence=new PreparedPlaybackEvidence(clip.samples,clip.frequency,AudioSettings.dspTime,
            Time.realtimeSinceStartupAsDouble,m_AudioSource.pitch);
    }
    private void ObservePreparedPlaybackEvidence()
    {
        if(m_PreparedPlaybackEvidence==null)return;
        if(m_PreparedPlaybackEvidenceGeneration!=m_FormalResponseGeneration || m_AudioSource==null ||
            m_AudioSource.clip!=m_PreparedPlaybackEvidenceClip) {m_PreparedPlaybackEvidence.Revoke("owner-or-clip-changed");return;}
        if(m_SceneTimePaused||AudioListener.pause)m_PreparedPlaybackEvidence.Revoke("paused");
        if(m_AudioSource.isPlaying)m_PreparedPlaybackEvidence.Observe(m_AudioSource.timeSamples,AudioSettings.dspTime,
            Time.realtimeSinceStartupAsDouble,m_AudioSource.pitch);
    }
    private JObject ReadPreparedPlaybackTerminal(AudioClip clip,int generation)
    {
        if(m_PreparedPlaybackEvidence==null||clip!=m_PreparedPlaybackEvidenceClip||generation!=m_PreparedPlaybackEvidenceGeneration)return null;
        if(m_PreparedPlaybackReceipt!=null)return m_PreparedPlaybackReceipt;
        bool current=generation==m_FormalResponseGeneration&&m_AudioSource!=null&&m_AudioSource.clip==clip&&!m_AudioSource.isPlaying;
        return m_PreparedPlaybackEvidence.Finish(AudioSettings.dspTime,Time.realtimeSinceStartupAsDouble,
            current,m_SceneTimePaused||AudioListener.pause);
    }
    private bool FinishPreparedPlaybackEvidence(AudioClip clip,int generation)
    {
        var receipt=ReadPreparedPlaybackTerminal(clip,generation);
        if(receipt==null)return false;
        if(m_PreparedPlaybackReceipt!=null)return (bool)m_PreparedPlaybackReceipt["complete"];
        m_PreparedPlaybackReceipt=receipt;
        m_PreparedPlaybackReceipt["generation"]=generation;
        Debug.Log("[TTS/LocalPlaybackTerminal] "+m_PreparedPlaybackReceipt.ToString(Formatting.None),this);
        return (bool)m_PreparedPlaybackReceipt["complete"];
    }
    private void StopSpeechAudio([CallerMemberName]string reason=null)
    {
        if(m_PreparedPlaybackReceipt==null)m_PreparedPlaybackEvidence?.Revoke(reason??"explicit-stop");
        if(m_AudioSource!=null)m_AudioSource.Stop();
        if(m_PreparedPlaybackEvidence!=null && m_PreparedPlaybackReceipt==null)
            FinishPreparedPlaybackEvidence(m_PreparedPlaybackEvidenceClip,m_PreparedPlaybackEvidenceGeneration);
    }
}
