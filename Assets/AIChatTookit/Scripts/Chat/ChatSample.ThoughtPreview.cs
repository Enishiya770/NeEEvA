using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

public partial class ChatSample
{
    // A view of one still-revocable listening reaction. It never feeds the spoken
    // queue, accepted conversation history, memory or the recorder's turn decision.
    private string m_ListeningThoughtText = "";
    private string m_ListeningThoughtSource = "";
    private string m_ListeningThoughtCheckedTranscript = "";
    private int m_ListeningThoughtCapture, m_ListeningThoughtInputRevision;
    private int m_ListeningThoughtVersion, m_ListeningThoughtEpoch;
    private float m_ListeningThoughtReceivedAt;
    private const float ListeningThoughtLifetime = 7f;

    public string PresentationThoughtText
    {
        get
        {
            MaintainListeningThought();
            return isActiveAndEnabled ? m_ListeningThoughtText : "";
        }
    }

    public int PresentationThoughtVersion => m_ListeningThoughtVersion;

    private void PublishListeningThought(string reaction, string sourceTranscript,
        int captureSerial, int inputRevision, int requestVersion, int epoch)
    {
        if (!m_EnableSpeculativeListening || requestVersion != m_SpeculativeRequestVersion ||
            epoch != m_ListeningThoughtEpoch || inputRevision != m_InputAudioRevision ||
            captureSerial <= 0 || captureSerial != m_PreparedBoundaryEvidence.CaptureSerial ||
            !m_PreparedBoundaryEvidence.Recording || IsAISpeaking || IsVoiceOutputPlaying ||
            !ListeningThoughtTranscriptCompatible(sourceTranscript, m_StreamingTranscript) ||
            !ListeningThoughtTranscriptCompatible(sourceTranscript, m_PreparedBoundaryEvidence.Transcript))
            return;

        string text = SanitizeListeningThought(reaction);
        if (text.Length == 0) { ClearListeningThought(); return; }
        m_ListeningThoughtText = text;
        m_ListeningThoughtSource = sourceTranscript ?? "";
        m_ListeningThoughtCheckedTranscript = m_StreamingTranscript ?? "";
        m_ListeningThoughtCapture = captureSerial;
        m_ListeningThoughtInputRevision = inputRevision;
        m_ListeningThoughtReceivedAt = Time.realtimeSinceStartup;
        m_ListeningThoughtVersion++;
    }

    private void MaintainListeningThought()
    {
        if (m_ListeningThoughtText.Length == 0) return;
        if (IsAISpeaking || IsVoiceOutputPlaying || !m_EnableSpeculativeListening ||
            m_ListeningThoughtInputRevision != m_InputAudioRevision ||
            m_ListeningThoughtCapture != m_PreparedBoundaryEvidence.CaptureSerial ||
            !m_PreparedBoundaryEvidence.Recording ||
            Time.realtimeSinceStartup - m_ListeningThoughtReceivedAt >= ListeningThoughtLifetime)
            ClearListeningThought();
    }

    private void ReconcileListeningThought(string transcript)
    {
        if (m_ListeningThoughtText.Length == 0 ||
            string.Equals(transcript, m_ListeningThoughtCheckedTranscript, StringComparison.Ordinal)) return;
        m_ListeningThoughtCheckedTranscript = transcript;
        // Appended words can negate, qualify or replace an earlier goal. Withdraw
        // the interpretation immediately; lexical continuity is not semantic proof.
        if (!ListeningThoughtTranscriptCompatible(m_ListeningThoughtSource, transcript))
            ClearListeningThought();
    }

    private static bool ListeningThoughtTranscriptCompatible(string source, string current)
    {
        return string.Equals(source ?? "", current ?? "", StringComparison.Ordinal);
    }

    private void ClearListeningThought()
    {
        if (m_ListeningThoughtText.Length == 0) return;
        m_ListeningThoughtText = "";
        m_ListeningThoughtSource = "";
        m_ListeningThoughtCheckedTranscript = "";
        m_ListeningThoughtVersion++;
    }

    private void InvalidateListeningThoughtPresentation()
    {
        // Disable/re-enable must not revive a request that was sent before the
        // view's owner was disabled. This epoch is separate from speech ownership.
        m_ListeningThoughtEpoch++;
        ClearIncrementalListeningPreparation();
        ClearListeningThought();
    }

    private static string SanitizeListeningThought(string reaction)
    {
        if (string.IsNullOrWhiteSpace(reaction)) return "";
        string text = reaction.Trim();
        // Only the dedicated prose field is displayable. Reject structured
        // payloads rather than stripping their wrapper and exposing the contents.
        if (text.Length > 512 || text.IndexOfAny(new[] { '<', '>', '{', '}', '`' }) >= 0 ||
            RoleOutputPublicBoundary.ProbeEnvelope(text) != RoleOutputPublicBoundary.EnvelopeProbe.Plain)
            return "";
        text = Regex.Replace(text, @"[\s\p{Cc}]+", " ").Trim();
        var elements = new StringInfo(text);
        return elements.LengthInTextElements <= 40 ? text : elements.SubstringByTextElements(0, 39) + "…";
    }
}
