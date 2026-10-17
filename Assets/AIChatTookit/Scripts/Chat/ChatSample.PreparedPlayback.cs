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
    private PendingSpeechChunk m_PreparedPlaybackChunk;
    private string m_PreparedPlaybackText;
    private bool m_PreparedPlaybackReported;
    private static double PlaybackDspQuantum()
    {
        AudioSettings.GetDSPBufferSize(out int samples,out int buffers);
        return AudioSettings.outputSampleRate>0?samples/(double)AudioSettings.outputSampleRate:0;
    }
    private void BeginPreparedPlaybackEvidence(AudioClip clip,int generation)
    {
        if(m_PreparedPlaybackEvidence!=null && m_PreparedPlaybackReceipt==null) {
            m_PreparedPlaybackEvidence.Revoke("playback-replaced");
            FinishPreparedPlaybackEvidence(m_PreparedPlaybackEvidenceClip,m_PreparedPlaybackEvidenceGeneration);
        }
        m_PreparedPlaybackEvidenceClip=clip;m_PreparedPlaybackEvidenceGeneration=generation;m_PreparedPlaybackReceipt=null;
        m_PreparedPlaybackChunk=null;m_PreparedPlaybackText=null;m_PreparedPlaybackReported=false;
        m_PreparedPlaybackEvidence=new PreparedPlaybackEvidence(clip.samples,clip.frequency,AudioSettings.dspTime,
            Time.realtimeSinceStartupAsDouble,m_AudioSource.pitch,PlaybackDspQuantum());
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
        bool current=generation==m_FormalResponseGeneration&&m_AudioSource!=null&&m_AudioSource.clip==clip;
        if(current && m_AudioSource.isPlaying)return null;
        return m_PreparedPlaybackEvidence.Freeze(AudioSettings.dspTime,Time.realtimeSinceStartupAsDouble,
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
        if((bool)m_PreparedPlaybackReceipt["complete"] && generation==m_FormalResponseGeneration && m_PreparedPlaybackChunk!=null) {
            m_PreparedPlaybackReported=true;
            RecordCompletedSpeechChunk(m_PreparedPlaybackChunk,m_PreparedPlaybackText,generation);
        }
        return (bool)m_PreparedPlaybackReceipt["complete"];
    }
    private void BindPreparedPlaybackChunk(PendingSpeechChunk chunk,string text)
    {
        // Freeze the audio's version and text, not a mutable queue selection.
        m_PreparedPlaybackChunk=new PendingSpeechChunk {RoundId=chunk.RoundId,Text=text,LanguageCode=chunk.LanguageCode,
            RoomState=chunk.RoomState,RoomStateVersion=chunk.RoomStateVersion,RoomPrefixOnly=chunk.RoomPrefixOnly,
            ProcessUnit=chunk.ProcessUnit,ProcessVersion=chunk.ProcessVersion};
        m_PreparedPlaybackText=text;
    }
    private void RecordCompletedSpeechChunk(PendingSpeechChunk chunk,string text,int generation)
    {
        ObserveActivitySpeech(chunk.ProcessUnit,"completed");m_AssistantHeardText.Append(text);
        RecordRoomSpeechPlayedForResult(chunk.RoomState,text,chunk.ProcessUnit?.CorrectedResultId);
        RecordWorkCompletedAudio(text,chunk.ProcessUnit?.CorrectedResultId,chunk.ProcessUnit,chunk.ProcessVersion);
        RecordReviewedGoalAnswerPlayedChunk(generation,text);
    }
    private void SettleEndedPreparedPlayback()
    {
        if(m_PreparedPlaybackEvidence!=null && m_PreparedPlaybackReceipt==null && !m_SceneTimePaused && !AudioListener.pause &&
            m_PreparedPlaybackEvidenceGeneration==m_FormalResponseGeneration && m_AudioSource!=null &&
            m_AudioSource.clip==m_PreparedPlaybackEvidenceClip && !m_AudioSource.isPlaying)
            FinishPreparedPlaybackEvidence(m_PreparedPlaybackEvidenceClip,m_PreparedPlaybackEvidenceGeneration);
    }
    private void StopSpeechAudio([CallerMemberName]string reason=null)
    {
        ObserveLatencyFillerProgress();
        SettleEndedPreparedPlayback(); // a late stop cannot retract an already drained owned clip
        if(IsOwnedLatencyFillerPlaying())FreezeOpeningPlayback(reason??"explicit-stop");
        if(m_PreparedPlaybackReceipt==null)m_PreparedPlaybackEvidence?.Revoke(reason??"explicit-stop");
        if(m_AudioSource!=null)m_AudioSource.Stop();
        if(m_PreparedPlaybackEvidence!=null && m_PreparedPlaybackReceipt==null)
            FinishPreparedPlaybackEvidence(m_PreparedPlaybackEvidenceClip,m_PreparedPlaybackEvidenceGeneration);
    }
}
