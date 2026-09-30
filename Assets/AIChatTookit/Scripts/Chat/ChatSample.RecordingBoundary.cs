using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private List<RecordingBoundarySource> m_LastCompletedBoundarySources = new List<RecordingBoundarySource>();
    private IncrementalListeningCandidate m_IncrementalSingingSourceCandidate;
    private List<RecordingBoundarySource> m_IncrementalSingingSources;
    private int m_IncrementalSingingPlaybackSequence;

    private void ClearIncrementalSingingMaterialSnapshot()
    { m_IncrementalSingingSourceCandidate = null; m_IncrementalSingingSources = null; m_IncrementalSingingPlaybackSequence = 0; }

    private void PrepareIncrementalSingingMaterialSnapshot(IncrementalListeningCandidate candidate)
    {
        ClearIncrementalSingingMaterialSnapshot();
        if (candidate == null || candidate.Domain != "singing" && candidate.Domain != "mixed") return;
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null) return;
        m_IncrementalSingingSourceCandidate = candidate;
        m_IncrementalSingingPlaybackSequence = m_RecordingPlaybackSequence;
        // This only keeps already-frozen local references, never a semantic choice,
        // server request, voice synthesis, or material mutation while user speaks.
        m_IncrementalSingingSources = m_LastCompletedBoundarySources.Where(sense.BoundarySourceStillCurrent).ToList();
    }

    private JObject IncrementalSingingMaterialFacts(IncrementalListeningCandidate candidate)
    {
        if (!ReferenceEquals(candidate, m_IncrementalSingingSourceCandidate) || m_IncrementalSingingSources == null ||
            m_IncrementalSingingPlaybackSequence != m_RecordingPlaybackSequence) return null;
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null || m_IncrementalSingingSources.Any(s => !sense.BoundarySourceStillCurrent(s))) return null;
        return new JObject { ["preparation"] = "read_only_last_playback_candidates", ["playbackSequence"] = m_IncrementalSingingPlaybackSequence,
            ["capture"] = candidate.Source.CaptureSerial, ["inputRevision"] = candidate.InputRevision, ["requestVersion"] = candidate.RequestVersion,
            ["materials"] = new JArray(m_IncrementalSingingSources.Select(s => s.Facts(true))),
            ["semanticSegments"] = candidate.SegmentSpans,
            ["selected"] = false, ["scope"] = "Partial semantic hypotheses only. Final accepted input must select and review source; text spans are not audio boundaries." };
    }

    private void BindRecordingBoundaryContext(RecordingSingingDecisionContext context, SenseVoiceSpeechToText sense)
    {
        if (sense == null) return;
        var completed = m_LastCompletedBoundarySources.ToList();
        int sequence = m_RecordingPlaybackSequence, epoch = m_WorkEpoch;
        long revision = m_AcceptedRecordingInputRevision;
        foreach (var material in context.Materials)
        {
            var evidence = sense.DescribeRecordingBoundaryEvidence(material.ClipRef);
            if (evidence == null) continue;
            var facts = RecordingSingingDecisionCompiler.BoundaryMaterialFacts(material);
            facts["boundaryEvidence"] = evidence; material.Description = facts.ToString(Newtonsoft.Json.Formatting.None);
        }
        context.BoundaryObligations = (JArray)m_RecordingBoundaryObligations.DeepClone();
        context.ObserveBoundaryProposal = (c, proposal, error) => {
            if (epoch == m_WorkEpoch && revision == m_AcceptedRecordingInputRevision)
                RetainRecordingBoundaryProposal(c, proposal, error);
        };
        context.ObserveBoundaryResult = (c, proposal, result) => {
            if (epoch == m_WorkEpoch && revision == m_AcceptedRecordingInputRevision)
                RetainRecordingBoundaryResult(c, proposal, result);
        };
        context.LastPlaybackBinding = new JObject { ["sequence"] = sequence,
            ["materials"] = new JArray(completed.Select(s => s.Facts(sense.BoundarySourceStillCurrent(s)))),
            ["scope"] = "Last completed actual playback sequence only. Partial or interrupted playback never replaces it. This is a candidate, not the current selection." };
        context.RefineBoundary = (decision, isCurrent, done) => ResolveBoundRecordingBoundary(context, decision, completed, sequence, sense,
            () => epoch == m_WorkEpoch && revision == m_AcceptedRecordingInputRevision, isCurrent, done);
    }

    private IEnumerator ResolveBoundRecordingBoundary(RecordingSingingDecisionContext context, JObject decision,
        List<RecordingBoundarySource> completed, int playbackSequence, SenseVoiceSpeechToText sense, Func<bool> ownerCurrent,
        Func<bool> requestCurrent, Action<JObject> done)
    {
        bool Current() => ownerCurrent() && (requestCurrent == null || requestCurrent());
        JObject Fail(string reason) => new JObject { ["status"] = "boundary_unresolved", ["reason"] = reason, ["range"] = null };
        var edit = decision["boundary_edit"] as JObject;
        string key = (string)(decision["materials"] as JArray)?.FirstOrDefault();
        var material = context.Materials.SingleOrDefault(m => m.Key == key);
        if (!Current() || material == null || edit == null) { done(Fail("source_binding_unavailable")); yield break; }
        bool last = (string)edit["target"] == "last_playback";
        bool segments = (string)edit["kind"] == "segments";
        string range = (string)decision["range"], reason = null;
        if (last && range != "current") { done(Fail("boundary-source-range-unavailable")); yield break; }
        var source = last ? completed.FirstOrDefault(s => s.Clip == material.ClipRef && s.Revision == material.Revision)
            : sense.SnapshotBoundarySource(material.ClipRef, range, segments ? "lead" : (string)edit["edge"], out reason);
        if (source == null) { done(Fail(reason ?? "source_binding_unavailable")); yield break; }
        if (source == null || source.Revision != material.Revision || !sense.BoundarySourceStillCurrent(source) ||
            last && playbackSequence != m_RecordingPlaybackSequence)
        { done(Fail(sense.BoundarySourceChangeReason(source) ?? "source_version_changed")); yield break; }
        if (!source.MatchesProposal(material, edit, out string proposalMismatch)) { done(Fail(proposalMismatch)); yield break; }
        if (segments)
        {
            if (!RecordingSingingDecisionCompiler.SegmentBoundaryAvailable(context)) { done(Fail("segment-boundary-provider-unavailable")); yield break; }
            var facts = RecordingSingingDecisionCompiler.BoundaryMaterialFacts(material);
            if (!RecordingSegmentIdentity.TrySelect(edit, facts["boundaryEvidence"] as JObject, out var selection, out reason)) { done(Fail(reason)); yield break; }
            // Candidate ASR windows can cover both ends; analysis uses fixed raw
            // coordinates. Current playback remains strictly inside its range.
            if (source.UsesRetainedEvidence) { source.Start = 0; source.End = source.SampleCount / (float)source.SampleRate; }
            var request = RecordingSegmentBoundary.Request(source, selection);
            JObject result = null;
            yield return context.RefineSegments(source, request, Current, value => result = value);
            if (!Current()) yield break;
            if (!sense.TryBindRecordingSegmentBoundary(source, edit, request, result, Current, out reason))
            { done(Fail(reason)); yield break; }
            done(result); yield break;
        }
        RecordingSingingDecisionCompiler.RetainVerifiedOppositeEdge(source, edit, context.BoundaryObligations);
        if (source.End - source.Start < 1f) { done(Fail("boundary-obligation-unresolved")); yield break; }
        yield return sense.RefineRecordingBoundary(source, edit, last ? playbackSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            () => Current() && (!last || playbackSequence == m_RecordingPlaybackSequence),
            () => ownerCurrent() && (!last || playbackSequence == m_RecordingPlaybackSequence), done);
    }
}
