using System;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // The delayed boolean survives the opener's natural end until the formal
    // handoff is committed. Only this exact clip can still be that opener.
    private AudioClip m_LatencyFillerClip;
    private AudioSource m_FirstAudioProgressSource;
    private AudioClip m_FirstAudioProgressClip;
    private AudioClip m_LatencyProgressClip;
    private int m_LatencyProgressSamples;
    private double m_LatencyProgressDsp;
    private bool m_LatencyProgressComplete;
    private PreparedPlaybackEvidence m_OpeningPlaybackEvidence;
    private JObject m_OpeningPlaybackReceipt;
    private int m_OpeningOwner,m_OpeningSerial;
    private string m_OpeningText,m_OpeningId;

    private void BeginOpeningPlaybackEvidence()
    {
        if(m_OpeningPlaybackEvidence!=null && m_OpeningPlaybackReceipt==null)FreezeOpeningPlayback("opening-replaced");
        m_LatencyProgressClip=m_LatencyFillerClip;m_LatencyProgressSamples=0;m_LatencyProgressComplete=false;
        m_OpeningOwner=m_FormalResponseGeneration;m_OpeningText=m_LatencyFillerText;
        m_OpeningId="opening:"+m_OpeningOwner+":"+(++m_OpeningSerial);m_OpeningPlaybackReceipt=null;
        m_OpeningPlaybackEvidence=new PreparedPlaybackEvidence(m_LatencyProgressClip.samples,m_LatencyProgressClip.frequency,
            AudioSettings.dspTime,Time.realtimeSinceStartupAsDouble,m_AudioSource.pitch,PlaybackDspQuantum());
    }

    private void FreezeOpeningPlayback(string reason=null)
    {
        if(m_OpeningPlaybackEvidence==null || m_OpeningPlaybackReceipt!=null)return;
        if(reason!=null)m_OpeningPlaybackEvidence.Revoke(reason);
        bool current=m_OpeningOwner==m_FormalResponseGeneration && m_AudioSource!=null && m_AudioSource.clip==m_LatencyProgressClip;
        m_OpeningPlaybackReceipt=m_OpeningPlaybackEvidence.Freeze(AudioSettings.dspTime,Time.realtimeSinceStartupAsDouble,
            current,m_SceneTimePaused||AudioListener.pause);
        m_LatencyProgressComplete=(bool)m_OpeningPlaybackReceipt["complete"];
        m_OpeningPlaybackReceipt["generation"]=m_OpeningOwner;m_OpeningPlaybackReceipt["id"]=m_OpeningId;
        m_OpeningPlaybackReceipt["candidateText"]=m_OpeningText;
        m_OpeningPlaybackReceipt["wholeTextPlayed"]=m_LatencyProgressComplete;
        m_OpeningPlaybackReceipt["playedSamples"]=m_LatencyProgressSamples;
        m_OpeningPlaybackReceipt["sourceSamples"]=m_LatencyProgressClip!=null?m_LatencyProgressClip.samples:0;
        if(current && m_LatencyProgressComplete)RecordActivityDialogue("assistant",m_OpeningText,m_OpeningId);
        Debug.Log("[Speech/OpeningReceipt] "+m_OpeningPlaybackReceipt.ToString(Formatting.None),this);
    }

    private void RecordOpeningInterruption()
    {
        // This opening has no formal SpeechUnit. Never look up a newer playing
        // unit or read samples from the shared source after it has changed hands.
        if(m_OpeningPlaybackReceipt==null)return;
        var receipt=(JObject)m_OpeningPlaybackReceipt.DeepClone();
        receipt["source"]="opening-interrupted";receipt["exactSpokenPrefixKnown"]=false;
        if(m_OpeningOwner==m_FormalResponseGeneration)m_LastInterruptedSpeechEvidence=receipt;
        Debug.Log("[Activity/InterruptedSpeech] "+receipt.ToString(Formatting.None),this);
    }

    private void ObserveLatencyFillerProgress()
    {
        if(m_OpeningPlaybackEvidence==null||m_OpeningPlaybackReceipt!=null)return;
        if(m_OpeningOwner!=m_FormalResponseGeneration||m_AudioSource==null||m_AudioSource.clip!=m_LatencyProgressClip)
        { FreezeOpeningPlayback("owner-or-clip-changed");return; }
        if(m_SceneTimePaused||AudioListener.pause){FreezeOpeningPlayback("paused");return;}
        if(m_AudioSource.isPlaying) {
            m_OpeningPlaybackEvidence.Observe(m_AudioSource.timeSamples,AudioSettings.dspTime,
                Time.realtimeSinceStartupAsDouble,m_AudioSource.pitch);
            m_LatencyProgressSamples=Math.Max(m_LatencyProgressSamples,m_AudioSource.timeSamples);
            m_LatencyProgressDsp=AudioSettings.dspTime;
        } else FreezeOpeningPlayback();
    }

    private static bool SourceHasSampleProgress(AudioSource source)
        => source != null && source.clip != null && source.isPlaying && source.timeSamples > 0;

    private bool HasVoiceOutputSampleProgress()
        => SourceHasSampleProgress(m_AudioSource) || SourceHasSampleProgress(m_HumStreamSecondaryAudioSource);

    private void ObserveVoiceOutputFirstAudio(bool playing, bool began)
    {
        ObserveLatencyFillerProgress();
        if (began)
        {
            m_FirstAudioProgressSource = m_AudioSource != null && m_AudioSource.isPlaying && m_AudioSource.clip != null
                ? m_AudioSource : m_HumStreamSecondaryAudioSource;
            m_FirstAudioProgressClip = m_FirstAudioProgressSource != null ? m_FirstAudioProgressSource.clip : null;
        }
        if (m_FirstAudioProgressSource == null) return;
        if (!playing || m_FirstAudioProgressClip == null || m_FirstAudioProgressSource.clip != m_FirstAudioProgressClip)
        {
            m_FirstAudioProgressSource = null; m_FirstAudioProgressClip = null;
            return;
        }
        if (SourceHasSampleProgress(m_FirstAudioProgressSource))
        {
            // Keep the busy/revision transition at Play. Only the latency receipt
            // waits for progress, and only this pending clip can consume it.
            ReportPerceivedFirstAudio();
            m_FirstAudioProgressSource = null; m_FirstAudioProgressClip = null;
        }
    }

    private bool IsOwnedLatencyFillerPlaying()
    {
        return m_LatencyFillerPlayed && m_LatencyFillerStartedAt >= 0f &&
            m_LatencyFillerClip != null && m_AudioSource != null &&
            m_AudioSource.clip == m_LatencyFillerClip && m_AudioSource.isPlaying;
    }

    private IEnumerator AwaitFormalAudioAdvancement(int generation, int roundId, AudioClip clip,
        Func<bool> completed, Action<bool> observed)
    {
        float issuedAt = Time.realtimeSinceStartup;
        int clipId = clip != null ? clip.GetInstanceID() : 0;
        string reason = "playback-timeout";
        Debug.Log($"[Timing/PlaybackEvidence] phase=play-issued generation={generation} round={roundId} clip={clipId} success=False");
        while (Time.realtimeSinceStartup - issuedAt < 2f)
        {
            if (generation != m_FormalResponseGeneration || m_SilencedSpeechRoundIds.Contains(roundId))
            { reason = "generation-or-round-revoked"; break; }
            if (clip == null || m_AudioSource == null || m_AudioSource.clip != clip)
            { reason = "clip-replaced-or-destroyed"; break; }
            // timeSamples is DSP playback progress, whereas Play()/isPlaying can
            // both become true and be cancelled before one sample advances.
            if (m_AudioSource.timeSamples > 0)
            {
                Debug.Log($"[Timing/PlaybackEvidence] phase=playback-advanced generation={generation} round={roundId} clip={clipId} " +
                    $"samples={m_AudioSource.timeSamples} playing={m_AudioSource.isPlaying} elapsed={Time.realtimeSinceStartup-issuedAt:F3}s success=True scope=audio-source-progress-not-human-audibility");
                observed?.Invoke(true);
                yield break;
            }
            // A streaming provider can Pause immediately after Play while its
            // first DSP read asks for more PCM. isPlaying=false is not terminal:
            // the same owned request may resume once rebuffering completes.
            // Completion, ownership and generation are the terminal evidence.
            if (completed != null && completed())
            { reason = "ended-before-observed-progress"; break; }
            yield return null;
        }
        Debug.LogWarning($"[Timing/PlaybackEvidence] phase=start-unconfirmed generation={generation} round={roundId} clip={clipId} " +
            $"elapsed={Time.realtimeSinceStartup-issuedAt:F3}s success=False reason={reason}");
        observed?.Invoke(false);
    }
}
