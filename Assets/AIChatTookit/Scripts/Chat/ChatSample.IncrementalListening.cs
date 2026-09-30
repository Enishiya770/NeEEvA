using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private IncrementalListeningCandidate m_IncrementalListeningCandidate;
    private object m_IncrementalListeningProvider;
    private string m_LastIncrementalContextObservationKey;
    private JObject m_IncrementalListeningContextObservation;

    // Shared by speech and singing preparation. This stores no accepted dialogue,
    // schedules no action, and never synthesizes or queues a speculative reply.
    private void StageIncrementalListeningPreparation(string response, PreparedTurnBoundaryEvidence source,
        int inputRevision, int requestVersion, float requestedAt, int thoughtEpoch)
    {
        string discarded = !m_EnableSpeculativeListening ? "listening-disabled" :
            inputRevision != m_InputAudioRevision ? "input-revision-changed" :
            requestVersion != m_SpeculativeRequestVersion ? "request-version-changed" :
            thoughtEpoch != m_ListeningThoughtEpoch ? "presentation-epoch-changed" :
            source.CaptureSerial != m_PreparedBoundaryEvidence.CaptureSerial ? "capture-changed" :
            !m_PreparedBoundaryEvidence.Recording ? "recording-already-closed" : null;
        if (discarded != null)
        {
            // A stale callback must not clear a newer valid owner's candidate.
            LogIncrementalListening("callback-discarded", discarded, null, source, inputRevision, requestVersion);
            return;
        }
        ClearIncrementalListeningPreparation();
        ClearListeningThought();
        if (!IncrementalListeningCandidate.TryCreate(response, source, inputRevision, requestVersion,
            requestedAt, Time.realtimeSinceStartup, out var candidate, out string reason))
        {
            LogIncrementalListening("rejected", reason, null, source, inputRevision, requestVersion);
            return;
        }
        m_IncrementalListeningCandidate = candidate;
        m_IncrementalListeningProvider = m_ChatSettings?.m_SpeechToText;
        PrepareIncrementalSingingMaterialSnapshot(candidate);
        if (candidate.CanPresent(m_StreamingTranscript) && candidate.CanPresent(m_PreparedBoundaryEvidence.Transcript))
            PublishListeningThought(candidate.Reaction, source.Transcript, source.CaptureSerial,
                inputRevision, requestVersion, thoughtEpoch);
        LogIncrementalListening("prepared", candidate.ExactSource(m_StreamingTranscript) ? "current-source" : "source-already-updated", candidate);
    }

    private JObject BuildIncrementalListeningFacts()
    {
        var candidate = m_IncrementalListeningCandidate;
        string reason = candidate == null ? "no-candidate" :
            !m_EnableSpeculativeListening ? "listening-disabled" :
            candidate.InputRevision != m_InputAudioRevision ? "input-revision-changed" :
            !ReferenceEquals(m_IncrementalListeningProvider, m_ChatSettings?.m_SpeechToText) ? "provider-changed" :
            m_FastLoopAcceptedAudioFacts == null ? "no-accepted-audio" :
            !ReferenceEquals(m_IncrementalListeningProvider, m_FastLoopInputFactsProvider) ? "provider-changed" :
            !FastLoopInputFactsCurrent ? "accepted-input-lease-not-current" :
            candidate.OwnershipFailure(m_FastLoopInputFactsCapture, m_InputAudioRevision, Time.realtimeSinceStartup);
        if (reason != null)
        {
            ObserveIncrementalContext("context-omitted", reason, candidate, null);
            return null;
        }
        var facts = candidate.ToFinalContext(m_LastUserMsg);
        facts["acceptedInputRevision"] = m_FastLoopAcceptedInputRevision;
        facts["preparedMaterials"] = IncrementalSingingMaterialFacts(candidate);
        ObserveIncrementalContext("context-ready", "requires-final-model-review", candidate, facts);
        return facts;
    }

    private void ObserveIncrementalContext(string phase, string reason, IncrementalListeningCandidate candidate, JObject facts)
    {
        var observation = new JObject {
            ["phase"] = phase, ["reason"] = reason,
            ["capture"] = candidate?.Source.CaptureSerial ?? m_FastLoopInputFactsCapture,
            ["inputRevision"] = candidate?.InputRevision ?? m_InputAudioRevision,
            ["requestVersion"] = candidate?.RequestVersion,
            ["acceptedInputRevision"] = m_FastLoopAcceptedInputRevision,
            ["formalGeneration"] = m_FormalResponseGeneration, ["workEpoch"] = m_WorkEpoch,
            ["finalMismatch"] = facts?["finalMismatch"]?.DeepClone(),
            ["preparedMaterialsPresent"] = facts?["preparedMaterials"] != null && facts["preparedMaterials"].Type != JTokenType.Null,
            ["requestAttached"] = false, ["modelUse"] = "unknown", ["executionAuthorized"] = false
        };
        m_IncrementalListeningContextObservation = observation;
        string key = observation.ToString(Newtonsoft.Json.Formatting.None);
        if (key == m_LastIncrementalContextObservationKey) return;
        m_LastIncrementalContextObservationKey = key;
        if (m_LogSpeculativeListening) Debug.Log("[IncrementalListening] " + key);
    }

    private void ClearIncrementalListeningPreparation()
    {
        if (m_IncrementalListeningCandidate != null)
        {
            var previous = m_IncrementalListeningCandidate;
            string reason = previous.Source.CaptureSerial != m_PreparedBoundaryEvidence.CaptureSerial ? "capture-changed" :
                previous.InputRevision != m_InputAudioRevision ? "input-revision-changed" : "owner-reset-or-replaced";
            LogIncrementalListening("cleared", reason, previous);
        }
        m_IncrementalListeningCandidate = null;
        m_IncrementalListeningProvider = null;
        m_LastIncrementalContextObservationKey = null;
        m_IncrementalListeningContextObservation = null;
        ClearIncrementalSingingMaterialSnapshot();
    }

    private void LogIncrementalListening(string phase, string reason, IncrementalListeningCandidate candidate,
        PreparedTurnBoundaryEvidence? source = null, int? inputRevision = null, int? requestVersion = null)
    {
        if (!m_LogSpeculativeListening) return;
        Debug.Log("[IncrementalListening] " + new JObject {
            ["phase"] = phase, ["reason"] = reason,
            ["capture"] = candidate?.Source.CaptureSerial ?? source?.CaptureSerial ?? m_PreparedBoundaryEvidence.CaptureSerial,
            ["inputRevision"] = candidate?.InputRevision ?? inputRevision ?? m_InputAudioRevision,
            ["requestVersion"] = candidate?.RequestVersion ?? requestVersion ?? m_SpeculativeRequestVersion,
            ["state"] = candidate?.State, ["domain"] = candidate?.Domain,
            ["evidenceCount"] = candidate?.EvidenceSpans.Count ?? 0,
            ["thoughtVisibleCandidate"] = m_ListeningThoughtText.Length > 0,
            ["speechPrepared"] = false, ["actionSubmitted"] = false,
            ["modelUse"] = "unknown", ["requestAttached"] = false
        }.ToString(Newtonsoft.Json.Formatting.None));
    }
}
