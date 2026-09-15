using UnityEngine;

public partial class SenseVoiceSpeechToText
{
    private ClosedSpeechEvidence m_ClosedSpeechEvidence;
    public int LastFinalCaptureSerial { get; private set; }
    public string LastFinalTranscriptSource { get; private set; } = "none";
    public string LastFinalRejectionReason { get; private set; } = "";
    public string LastFinalRawText { get; private set; } = "";

    private void BeginClosedSpeechCapture()
    {
        m_ClosedSpeechEvidence = null;
        LastFinalCaptureSerial = InputCaptureSessionSerial;
        LastFinalTranscriptSource = "pending";
        LastFinalRejectionReason = LastFinalRawText = "";
    }

    public void RetainClosedSpeechEvidence(ClosedSpeechEvidence evidence)
    {
        if (evidence == null || evidence.CaptureSessionSerial != InputCaptureSessionSerial ||
            evidence.NormalizedText != ClosedSpeechEvidence.Normalize(evidence.Text)) return;
        m_ClosedSpeechEvidence = evidence.Copy();
    }

    private void PublishFinalTranscriptOutcome(int capture, string source, string rejection, string rawText)
    {
        if (capture != InputCaptureSessionSerial) return;
        LastFinalCaptureSerial = capture;
        LastFinalTranscriptSource = source ?? "none";
        LastFinalRejectionReason = rejection ?? "";
        LastFinalRawText = rawText ?? "";
        Debug.Log($"[ASR/Final] capture={capture} source={LastFinalTranscriptSource} " +
            $"reason={LastFinalRejectionReason} rawTextChars={LastFinalRawText.Length} " +
            $"completedAt={Time.realtimeSinceStartup:F3}");
    }

    // Completed full previews have not necessarily been published as a FINAL
    // request. Bind their exact raw text before the one downstream delivery.
    public void PublishReusedFinalTranscript(int capture, string rawText)
    {
        if (capture != InputCaptureSessionSerial) return;
        bool valid = !string.IsNullOrWhiteSpace(rawText);
        if (valid) LastText = rawText;
        PublishFinalTranscriptOutcome(capture, valid ? "full-preview" : "rejected",
            valid ? "" : "preview-empty", rawText);
    }

    private bool TryRecoverClosedTranscript(int capture, bool terminal, string rejection, string rawFinalText,
        out string recovered, string rejectedText = "", bool rawVadHasSpeech = false, int rawVadSpeechMs = 0)
    {
        recovered = "";
        if (!ClosedSpeechEvidenceTracker.MayRecover(m_ClosedSpeechEvidence,
            InputCaptureSessionSerial, capture, terminal, rejection, rawFinalText, Time.realtimeSinceStartup,
            rejectedText, rawVadHasSpeech, rawVadSpeechMs))
            return false;
        recovered = m_ClosedSpeechEvidence.Text;
        // Consume once. Keep LastNoSpeech and the raw final outcome unchanged:
        // this is an explicitly identified recovery, not a successful final VAD.
        m_ClosedSpeechEvidence = null;
        LastText = recovered;
        return true;
    }

    private static string FinalRejectionReason(Response response)
    {
        if (response == null) return "invalid-response";
        if (response.audio_event == "SelfSpeech" || response.speaker_kind == "ai") return "self-speech";
        if (!string.IsNullOrWhiteSpace(response.no_speech_reason)) return response.no_speech_reason;
        if (response.no_speech) return "unknown-no-speech"; // Never infer VAD from an old server's empty result.
        return string.IsNullOrWhiteSpace(response.text) ? "recognizer-empty" : "";
    }
}
