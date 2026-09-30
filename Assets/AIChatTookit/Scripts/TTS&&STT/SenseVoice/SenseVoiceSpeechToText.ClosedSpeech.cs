using UnityEngine;

public partial class SenseVoiceSpeechToText
{
    private ClosedSpeechEvidence m_ClosedSpeechEvidence;
    public int LastFinalCaptureSerial { get; private set; }
    public string LastFinalTranscriptSource { get; private set; } = "none";
    public string LastFinalRejectionReason { get; private set; } = "";
    public string LastFinalRawText { get; private set; } = "";
    public string LastFinalPerceivedText { get; private set; } = "";

    private void BeginClosedSpeechCapture()
    {
        m_ClosedSpeechEvidence = null;
        LastFinalCaptureSerial = InputCaptureSessionSerial;
        LastFinalTranscriptSource = "pending";
        LastFinalRejectionReason = LastFinalRawText = LastFinalPerceivedText = "";
    }

    public void RetainClosedSpeechEvidence(ClosedSpeechEvidence evidence)
    {
        if (evidence == null || evidence.CaptureSessionSerial != InputCaptureSessionSerial ||
            evidence.NormalizedText != ClosedSpeechEvidence.Normalize(evidence.Text)) return;
        m_ClosedSpeechEvidence = evidence.Copy();
    }

    private void PublishFinalTranscriptOutcome(int capture, string source, string rejection, string rawText,
        string perceivedText = null)
    {
        if (capture != InputCaptureSessionSerial) return;
        LastFinalCaptureSerial = capture;
        LastFinalTranscriptSource = source ?? "none";
        LastFinalRejectionReason = rejection ?? "";
        LastFinalRawText = rawText ?? "";
        LastFinalPerceivedText = perceivedText ?? rawText ?? "";
        Debug.Log($"[ASR/Final] capture={capture} source={LastFinalTranscriptSource} " +
            $"reason={LastFinalRejectionReason} rawTextChars={LastFinalRawText.Length} " +
            $"completedAt={Time.realtimeSinceStartup:F3}");
    }

    // Completed full previews have not necessarily been published as a FINAL
    // request. Bind their exact raw text before the one downstream delivery.
    public void PublishReusedFinalTranscript(int capture, string rawText, string perceivedText = null)
    {
        if (capture != InputCaptureSessionSerial) return;
        bool valid = !string.IsNullOrWhiteSpace(rawText);
        if (valid) LastText = rawText;
        PublishFinalTranscriptOutcome(capture, valid ? "full-preview" : "rejected",
            valid ? "" : "preview-empty", rawText, perceivedText);
    }

    // Match the actual delivered envelope, not a guessed metadata-stripped
    // string. Raw transcript equality remains required by every consumer.
    public bool MatchesFinalTranscriptCallback(int capture, string callbackText) =>
        capture > 0 && capture == InputCaptureSessionSerial && capture == LastFinalCaptureSerial &&
        !string.IsNullOrWhiteSpace(callbackText) &&
        (callbackText == LastFinalPerceivedText || callbackText == LastFinalRawText ||
            LastFinalTranscriptSource == "closed-stream-recovery" && callbackText == LastText);

    public bool LastFinalInputFailed => LastFinalCaptureSerial == InputCaptureSessionSerial &&
        LastFinalTranscriptSource == "rejected" &&
        (LastFinalRejectionReason == "http-error" || LastFinalRejectionReason == "invalid-response" ||
            LastFinalRejectionReason == "request-no-result" || LastFinalRejectionReason == "input-error");

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
