using System;
using System.Collections.Generic;
using System.Globalization;

public partial class SenseVoiceSpeechToText
{
    // Runtime-only aliases. Long IDs stay internal/accepted for exact legacy calls;
    // a fresh component/session namespace cannot reinterpret an old short handle.
    private readonly string m_ClipNamespace = Guid.NewGuid().ToString("N").Substring(0, 8);
    private int m_NextClipHandle;
    private sealed class ClipIdentity
    {
        public string FullRef, ShortRef;
    }
    private readonly Dictionary<int, ClipIdentity> m_ClipIdentities = new Dictionary<int, ClipIdentity>();
    private sealed class CaptureOverlap
    {
        public int EarlierSession;
        public float PrefixSeconds;
    }
    private readonly Dictionary<int, CaptureOverlap> m_CaptureOverlaps = new Dictionary<int, CaptureOverlap>();

    public int InputCaptureSessionSerial => m_LiveRecordingCandidateSessionSerial;

    public void SetInputCaptureOverlap(int earlierSession, float prefixSeconds)
    {
        if (earlierSession <= 0 || earlierSession >= m_LiveRecordingCandidateSessionSerial ||
            prefixSeconds <= 0f || float.IsNaN(prefixSeconds) || float.IsInfinity(prefixSeconds)) return;
        m_CaptureOverlaps[m_LiveRecordingCandidateSessionSerial] = new CaptureOverlap {
            EarlierSession = earlierSession, PrefixSeconds = prefixSeconds };
    }

    private void EnsureClipIdentity(PracticePhrase phrase)
    {
        if (phrase.FullClipRef != null) return; // Already published: never rename on admission.
        ClipIdentity identity;
        if (phrase.CaptureSessionSerial <= 0 ||
            !m_ClipIdentities.TryGetValue(phrase.CaptureSessionSerial, out identity))
        {
            identity = new ClipIdentity {
                FullRef = phrase.ClipRef,
                ShortRef = "clip:" + m_ClipNamespace + "-" + (++m_NextClipHandle).ToString(CultureInfo.InvariantCulture)
            };
            if (phrase.CaptureSessionSerial > 0) m_ClipIdentities.Add(phrase.CaptureSessionSerial, identity);
        }
        phrase.FullClipRef = identity.FullRef;
        phrase.ClipRef = identity.ShortRef;
    }

    private static bool SameRecordingIdentity(PracticePhrase a, PracticePhrase b)
    {
        if (a == null || b == null) return false;
        if (a.CaptureSessionSerial > 0 || b.CaptureSessionSerial > 0)
            return a.CaptureSessionSerial > 0 && a.CaptureSessionSerial == b.CaptureSessionSerial;
        return ReferenceEquals(a, b) || (!string.IsNullOrEmpty(a.ClipRef) && a.ClipRef == b.ClipRef);
    }

    private static bool MatchesClipReference(PracticePhrase phrase, string reference) => phrase != null &&
        !string.IsNullOrWhiteSpace(reference) && (phrase.ClipRef == reference.Trim() || phrase.FullClipRef == reference.Trim());

    private PracticePhrase FindSingingClip(string reference) =>
        m_PracticePhrases.Find(p => MatchesClipReference(p, reference)) ??
        m_QuarantinedSingingCandidates.Find(c => MatchesClipReference(c.Phrase, reference))?.Phrase;

    public string CanonicalSingingClipReference(string reference) => FindSingingClip(reference)?.ClipRef ?? "";

    // Observation only. Ready says whether audio can execute, not what was sung,
    // whether there was singing, or whose voice this is. No semantic decision here.
    public string DescribeSingingClipObservation(string reference)
    {
        var phrase = FindSingingClip(reference);
        if (phrase == null) return "";
        var evidence = phrase.LatestRecordingEvidence ?? phrase.RecordingEvidence;
        string Compact(string text)
        {
            text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > 180 ? text.Substring(0, 180) + "…" : text;
        }
        string facts = $" recording_transcript=\"{Compact(evidence?.Text)}\"" +
            $" acoustic_mode={evidence?.AcousticMode ?? "unavailable"}" +
            " singing_p=" + (evidence == null ? "unavailable" : evidence.SingingProbability.ToString("F3", CultureInfo.InvariantCulture)) +
            $" asr_event={Compact(evidence?.AudioEvent)} content_verdict=undecided";
        if (m_CaptureOverlaps.TryGetValue(phrase.CaptureSessionSerial, out var overlap))
        {
            var earlier = m_PracticePhrases.Find(p => p.CaptureSessionSerial == overlap.EarlierSession) ??
                m_QuarantinedSingingCandidates.Find(c => c.Phrase.CaptureSessionSerial == overlap.EarlierSession)?.Phrase;
            facts += $" shared_prefix_raw=[0,{overlap.PrefixSeconds.ToString("F2", CultureInfo.InvariantCulture)}]" +
                $" earlier_ref={earlier?.ClipRef ?? "not_retained"} overlap_not_repeat=true speaker_continuity=unknown";
        }
        return facts;
    }
}
