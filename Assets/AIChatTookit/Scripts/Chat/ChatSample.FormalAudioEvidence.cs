using System;
using System.Collections;
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

    private void ObserveLatencyFillerProgress()
    {
        if(m_LatencyFillerClip==null)return;
        if(m_LatencyProgressClip!=m_LatencyFillerClip) {
            m_LatencyProgressClip=m_LatencyFillerClip;m_LatencyProgressSamples=0;m_LatencyProgressComplete=false;
        }
        if(m_AudioSource==null||m_AudioSource.clip!=m_LatencyProgressClip)return;
        if(m_AudioSource.isPlaying) {
            m_LatencyProgressSamples=Math.Max(m_LatencyProgressSamples,m_AudioSource.timeSamples);
            m_LatencyProgressDsp=AudioSettings.dspTime;
        } else if(m_LatencyProgressSamples>0 &&
            (m_LatencyProgressClip.samples-m_LatencyProgressSamples)/(double)Math.Max(1,m_LatencyProgressClip.frequency)<=
                Math.Min(.10,Math.Max(0,AudioSettings.dspTime-m_LatencyProgressDsp))+.05)
            m_LatencyProgressComplete=true;
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
