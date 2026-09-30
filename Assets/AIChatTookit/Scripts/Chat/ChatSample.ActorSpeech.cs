using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("Silently prepare the first fast-loop draft sentence while its actor intent is judged. Playback still requires the approved final draft.")]
    private bool m_EnableActorSpeechPreparation = true;

    // This is an audio cache for one unpublished draft, never a public speech queue.
    // A judge may discard it; only the existing speech consumers can adopt it.
    private sealed class ActorSpeechPreparation
    {
        public ActorDraft Draft;
        public string DraftId, FullText, InputLanguage, ChunkText, ResolvedLanguage;
        public int DraftRevision, Generation, Epoch, RoundId;
        public long OutputRevision;
        public TTS Provider;
        public TTS.PreparedSpeechStream Stream;
        public AudioClip Clip;
        public bool Approved, Done;
        public float StartedAt;
        public ConversationTrace Trace;
    }

    private ActorSpeechPreparation m_ActorSpeechPreparation;
    private const float k_ActorSpeechPreparationTimeoutSeconds = 25f;

    private void BeginActorSpeechPreparation(ActorDraft draft, string language, int generation, long outputRevision)
    {
        CancelActorSpeechPreparation("superseded");
        TTS provider = m_ChatSettings?.m_TextToSpeech;
        if (draft == null || string.IsNullOrEmpty(draft.Id) || string.IsNullOrWhiteSpace(draft.Text) ||
            provider == null || generation != m_FormalResponseGeneration || outputRevision != m_FastLoop.OutputRevision ||
            !isActiveAndEnabled || m_SceneVisionApplicationPaused || m_UserSpeechActiveForAutonomy ||
            !m_EnableFastTaskLoop || !m_EnableActorIntentCompiler || !m_EnableActorSpeechPreparation || !provider.CanPrefetchNextSpeech ||
            m_SentenceBuffer.Length != 0 || m_FirstChunkFlushed || m_PendingChunks.Count != 0 || m_PendingClips.Count != 0 ||
            m_PreparedSingingBridgeClip != null || m_SingingBridgeTtsInFlight || !string.IsNullOrEmpty(m_PreparedSingingBridgeText) ||
            m_HumBackPreparingCarrier || m_HoldSpeechForSongMemoryResult || m_HoldSpeechForHumBackResult ||
            m_RoundIsInner || m_RoundRepeatHold || m_RoundSilencedForRepeat || HoldSingingSpeechUntilValidation() ||
            CurrentMotionResponseRejected || m_SilencedSpeechRoundIds.Contains(m_CurrentSpeechRoundId)) return;

        bool streaming = provider.SupportsStreamingPlayback && provider.SupportsStreamingLookahead;
        if (provider.SupportsStreamingPlayback && !streaming) return;
        // A plain actor draft has one language span. Tagged/partial-tag input and
        // existing prepared openers can change FlushCompleteSentences' boundary.
        if (FindPotentialAgentTagStart(draft.Text) >= 0) return;
        int boundary = FindFlushBoundary(draft.Text, true);
        string first = StripAgentTagsForTTS((boundary < 0 ? draft.Text : draft.Text.Substring(0, boundary + 1)).Trim());
        if (string.IsNullOrWhiteSpace(first) || IsPurePunctuation(first)) return;
        SpeechText speech = provider.ResolveSpeech(new SpeechText(first, language));
        if (!string.Equals(speech.Text, first, StringComparison.Ordinal)) return;

        var slot = new ActorSpeechPreparation {
            Draft = draft, DraftId = draft.Id, DraftRevision = draft.Revision, FullText = draft.Text,
            InputLanguage = language, ChunkText = first, ResolvedLanguage = speech.LanguageCode,
            Generation = generation, Epoch = m_WorkEpoch, OutputRevision = outputRevision,
            RoundId = m_CurrentSpeechRoundId, Provider = provider, StartedAt = Time.realtimeSinceStartup,
            Trace = m_ResponseTrace?.Child("actor-speech")
        };
        m_ActorSpeechPreparation = slot;
        TraceActorSpeechPreparation(slot, "prepare", streaming ? "pcm" : "wav");
        var previousTrace = provider.TraceContext;
        provider.TraceContext = slot.Trace;
        try
        {
            if (streaming)
            {
                var prepared = provider.PrepareStreamingSpeech(speech);
                // A provider may synchronously cancel/reenter while acquiring its
                // handle. The old slot cannot retain a handle returned after that.
                if (!ReferenceEquals(m_ActorSpeechPreparation, slot) || !IsActorSpeechPreparationCurrent(slot))
                {
                    prepared?.Dispose();
                    if (ReferenceEquals(m_ActorSpeechPreparation, slot)) CancelActorSpeechPreparation("owner-invalidated");
                }
                else
                {
                    slot.Stream = prepared;
                    slot.Done = true; // The handle is ready; its PCM can still be arriving.
                    if (prepared == null) CancelActorSpeechPreparation("provider-busy");
                }
            }
            else
            {
                provider.PrepareSpeech(speech, (clip, ignored) => {
                    if (!ReferenceEquals(m_ActorSpeechPreparation, slot) || !IsActorSpeechPreparationCurrent(slot))
                    {
                        if (clip != null) Destroy(clip);
                        if (ReferenceEquals(m_ActorSpeechPreparation, slot)) CancelActorSpeechPreparation("stale-callback");
                        return;
                    }
                    slot.Done = true;
                    slot.Clip = clip;
                    if (clip == null) CancelActorSpeechPreparation("prepare-failed");
                });
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(m_ActorSpeechPreparation, slot)) CancelActorSpeechPreparation("prepare-exception");
            Debug.LogWarning("[ActorSpeech] Silent preparation failed: " + exception.GetType().Name, this);
        }
        finally
        {
            if (ReferenceEquals(provider.TraceContext, slot.Trace)) provider.TraceContext = previousTrace;
        }
        if (ReferenceEquals(m_ActorSpeechPreparation, slot)) StartCoroutine(WatchActorSpeechPreparation(slot));
    }

    private bool IsActorSpeechPreparationCurrent(ActorSpeechPreparation slot) =>
        slot != null && slot.Generation == m_FormalResponseGeneration && slot.Epoch == m_WorkEpoch &&
        slot.OutputRevision == m_FastLoop.OutputRevision && slot.RoundId == m_CurrentSpeechRoundId &&
        slot.Draft != null && slot.Draft.Id == slot.DraftId && slot.Draft.Revision == slot.DraftRevision &&
        string.Equals(slot.Draft.Text, slot.FullText, StringComparison.Ordinal) &&
        ReferenceEquals(slot.Provider, m_ChatSettings?.m_TextToSpeech) && slot.Provider != null &&
        isActiveAndEnabled && !m_SceneVisionApplicationPaused && !m_UserSpeechActiveForAutonomy &&
        m_EnableFastTaskLoop && m_EnableActorIntentCompiler && m_EnableActorSpeechPreparation && !m_SilencedSpeechRoundIds.Contains(slot.RoundId) &&
        Time.realtimeSinceStartup - slot.StartedAt < k_ActorSpeechPreparationTimeoutSeconds;

    private IEnumerator WatchActorSpeechPreparation(ActorSpeechPreparation slot)
    {
        while (ReferenceEquals(m_ActorSpeechPreparation, slot))
        {
            if (!IsActorSpeechPreparationCurrent(slot))
            {
                CancelActorSpeechPreparation(Time.realtimeSinceStartup - slot.StartedAt >= k_ActorSpeechPreparationTimeoutSeconds
                    ? "prepare-timeout" : "owner-invalidated");
                yield break;
            }
            yield return null;
        }
    }

    private void ResolveActorSpeechPreparation(string finalText, string language, int generation)
    {
        var slot = m_ActorSpeechPreparation;
        if (slot == null) return;
        if (!IsActorSpeechPreparationCurrent(slot) || generation != slot.Generation ||
            string.IsNullOrWhiteSpace(finalText) || !string.Equals(slot.FullText, finalText, StringComparison.Ordinal) ||
            !string.Equals(slot.InputLanguage, language, StringComparison.Ordinal))
        { CancelActorSpeechPreparation("final-draft-mismatch"); return; }
        if (slot.Approved) return;
        slot.Approved = true;
        TraceActorSpeechPreparation(slot, "approve", "same-final-draft");
    }

    private ActorSpeechPreparation MatchActorSpeechPreparation(int generation, int roundId, string text, string language, TTS provider)
    {
        var slot = m_ActorSpeechPreparation;
        if (slot == null) return null;
        if (!IsActorSpeechPreparationCurrent(slot))
        { CancelActorSpeechPreparation("owner-invalidated"); return null; }
        if (!slot.Approved) return null;
        if (generation != slot.Generation || roundId != slot.RoundId || !ReferenceEquals(provider, slot.Provider) ||
            !string.Equals(text, slot.ChunkText, StringComparison.Ordinal) ||
            !string.Equals(language, slot.ResolvedLanguage, StringComparison.Ordinal))
        { CancelActorSpeechPreparation("consumer-mismatch"); return null; }
        return slot;
    }

    private bool IsActorPreparedClipPending(int generation, int roundId, string text, string language, TTS provider)
    {
        var slot = MatchActorSpeechPreparation(generation, roundId, text, language, provider);
        return slot != null && slot.Stream == null && !slot.Done;
    }

    private PendingSpeechClip TakeActorSpeechPreparation(int generation, int roundId, string text, string language, TTS provider)
    {
        var slot = MatchActorSpeechPreparation(generation, roundId, text, language, provider);
        if (slot == null || !slot.Done || (slot.Clip == null && slot.Stream == null)) return null;
        m_ActorSpeechPreparation = null;
        var taken = new PendingSpeechClip {
            RoundId = roundId, Text = text, LanguageCode = language, Clip = slot.Clip, Stream = slot.Stream
        };
        slot.Clip = null;
        slot.Stream = null;
        TraceActorSpeechPreparation(slot, "adopt", "formal-consumer");
        return taken;
    }

    private void CancelActorSpeechPreparation(string reason)
    {
        var slot = m_ActorSpeechPreparation;
        if (slot == null) return;
        m_ActorSpeechPreparation = null; // A late callback may only destroy its own returned clip.
        var stream = slot.Stream;
        slot.Stream = null;
        stream?.Dispose(); // A stream handle cancels only this request, never the provider's newer job.
        if (slot.Clip != null) Destroy(slot.Clip);
        slot.Clip = null;
        // PrepareSpeech has no request-scoped cancellation token. Do not call its
        // global CancelPreparedSpeech: another subsystem may now own that slot.
        TraceActorSpeechPreparation(slot, "discard", reason);
    }

    private void TraceActorSpeechPreparation(ActorSpeechPreparation slot, string phase, string reason)
    {
        float seconds = Mathf.Max(0f, Time.realtimeSinceStartup - slot.StartedAt);
        slot.Trace?.Mark("actor_speech." + phase, new JObject {
            ["draftId"] = slot.DraftId, ["revision"] = slot.DraftRevision,
            ["generation"] = slot.Generation, ["roundId"] = slot.RoundId,
            ["characters"] = slot.ChunkText.Length, ["seconds"] = seconds, ["reason"] = reason,
            ["scope"] = "private audio preparation; not playback evidence"
        });
        if (m_LogStreamTimings)
            Debug.Log($"[ActorSpeech/{phase}] draft={slot.DraftId} revision={slot.DraftRevision} round={slot.RoundId} seconds={seconds:F3} reason={reason}", this);
    }
}
