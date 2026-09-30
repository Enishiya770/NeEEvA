using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public partial class SenseVoiceSpeechToText
{
    private float m_BoundaryTranscriptOriginSeconds;
    // Called once when a detached evidence snapshot is captured. A later ASR
    // response must never lend its transcript segments to an earlier PCM.
    private void CaptureBoundaryTranscriptEvidence(SingingEvidenceSnapshot evidence)
    {
        if (evidence == null || evidence.RawWavBytesStoredLength <= 44) return;
        var segments = new JArray();
        if (m_LastTurnSegmentsCaptureSessionSerial == evidence.CaptureSessionSerial &&
            m_LastSegmentedPrimaryText == evidence.Text && m_LastTurnSegments != null)
            foreach (var s in m_LastTurnSegments.Where(s => s != null && s.status == "ok"))
            {
                void Add(string text, float start, float end, string origin)
                {
                    // Segment coordinates use content audio, unlike the pitch
                    // timeline which can begin inside a long recording's tail.
                    start += m_BoundaryTranscriptOriginSeconds; end += m_BoundaryTranscriptOriginSeconds;
                    if (string.IsNullOrWhiteSpace(text) || !BoundaryFinite(start) || !BoundaryFinite(end) ||
                        start < 0 || end <= start || end > evidence.RawSeconds + .025f) return;
                    segments.Add(new JObject { ["id"] = s.id, ["text"] = text,
                        ["start_seconds"] = start, ["end_seconds"] = end, ["origin"] = origin,
                        ["region"] = s.region, ["status"] = "ok", ["word_aligned"] = false });
                }
                Add(s.text, s.start_seconds, s.end_seconds, "segment");
                if (s.alternatives != null) foreach (var a in s.alternatives.Where(a => a != null && string.IsNullOrEmpty(a.error)))
                    Add(a.text, a.start_seconds, a.end_seconds, "alternative");
            }
        if (!RecordingSegmentIdentity.TryWaveFormat(evidence.RawWavBytes, out int sampleRate, out long sampleCount)) return;
        evidence.BoundaryTranscriptEvidence = RecordingSegmentIdentity.CreateEvidence(
            RecordingBoundarySource.Hash(evidence.RawWavBytes), sampleRate, sampleCount,
            evidence.RecordingId, evidence.CaptureSessionSerial, segments);
    }

    public JObject DescribeRecordingBoundaryEvidence(string clip)
    {
        var p = FindSingingClip(clip); var e = p?.LatestRecordingEvidence ?? p?.RecordingEvidence;
        return e?.BoundaryTranscriptEvidence == null ? null : (JObject)e.BoundaryTranscriptEvidence.DeepClone();
    }

    private JArray BoundaryTranscriptSegments(SingingEvidenceSnapshot e) =>
        e?.BoundaryTranscriptEvidence?["segments"] is JArray segments
            ? (JArray)segments.DeepClone() : new JArray();
}
