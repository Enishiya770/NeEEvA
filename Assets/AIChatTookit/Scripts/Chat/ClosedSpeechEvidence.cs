using System;
using System.Text;

// An observation of a sealed microphone capture, never a movement command.
[Serializable]
public sealed class ClosedSpeechEvidence
{
    public int CaptureSessionSerial, Revision, AudioMs, SubmittedAudioMs;
    public float CapturedAt, ObservedAt, ClosedAt, StableForSeconds;
    public string Text = "", NormalizedText = "", CloseReason = "", Source = "closed-stream";

    public ClosedSpeechEvidence Copy() => (ClosedSpeechEvidence)MemberwiseClone();

    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (char c in value ?? "")
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }
}

// Pure state machine shared by capture publication and same-capture recovery tests.
public sealed class ClosedSpeechEvidenceTracker
{
    private int capture, revision, audioMs, repeats;
    private float capturedAt, observedAt = -1f, stableSince = -1f;
    private string text = "", key = "";
    private bool voiced, serverStable, sealedCapture;

    public void Begin(int captureSerial, float startedAt)
    {
        capture = captureSerial; capturedAt = startedAt;
        revision = audioMs = repeats = 0;
        observedAt = stableSince = -1f;
        text = key = "";
        voiced = serverStable = sealedCapture = false;
    }

    public void Observe(int captureSerial, string transcript, string stableText, int coveredAudioMs,
        float now, bool hasSpeechEvidence)
    {
        if (sealedCapture || captureSerial != capture || capture <= 0 || coveredAudioMs <= audioMs ||
            !Finite(now) || now < observedAt) return;
        audioMs = coveredAudioMs; observedAt = now;
        voiced |= hasSpeechEvidence;
        string next = (transcript ?? "").Trim(), nextKey = ClosedSpeechEvidence.Normalize(next);
        // Empty packets and rollbacks invalidate the candidate too. Retaining the
        // last nonempty partial here would conceal a later negation or correction.
        if (nextKey != key)
        {
            key = nextKey; text = next; stableSince = now; repeats = 1; revision++;
        }
        else { text = next; repeats++; }
        serverStable = key.Length > 0 && ClosedSpeechEvidence.Normalize(stableText) == key;
    }

    public bool TrySeal(int captureSerial, float now, int submittedAudioMs, bool endConfirmed,
        bool coversQuietTail, bool textRevisionPending, bool ordinarySpeech, string reason,
        out ClosedSpeechEvidence evidence)
    {
        evidence = null;
        if (sealedCapture || captureSerial != capture || capture <= 0 || !endConfirmed ||
            !ordinarySpeech || !coversQuietTail || textRevisionPending || !voiced ||
            key.Length == 0 || text.Length > 120 || audioMs < 160 || submittedAudioMs > 6500 ||
            submittedAudioMs < audioMs || submittedAudioMs - audioMs > 1000 ||
            !Finite(now) || !Finite(capturedAt) || now < observedAt || now - observedAt > 1.25f ||
            stableSince < 0f || now - stableSince < .35f || (repeats < 2 && !serverStable) ||
            string.IsNullOrWhiteSpace(reason) || reason == "manual-disable" || reason == "llm-interrupt_user")
            return false;
        sealedCapture = true;
        evidence = new ClosedSpeechEvidence {
            CaptureSessionSerial = capture, Revision = revision, AudioMs = audioMs,
            SubmittedAudioMs = submittedAudioMs, CapturedAt = capturedAt, ObservedAt = observedAt,
            ClosedAt = now, StableForSeconds = now - stableSince, Text = text,
            NormalizedText = key, CloseReason = reason
        };
        return true;
    }

    public static bool MayRecover(ClosedSpeechEvidence evidence, int currentCapture, int requestCapture,
        bool terminal, string rejectionReason, string rawFinalText, float now,
        string rejectedText = "", bool rawVadHasSpeech = false, int rawVadSpeechMs = 0)
    {
        // The former blanket <=5-character/EMO_UNKNOWN/guest gate can reject a
        // correctly decoded short phrase. Recover that conflict only when raw
        // speech VAD (before any singing rescue) and the sealed stream agree
        // with the COMPLETE decoder text. No word is interpreted as a command.
        bool agreedShortSpeech = evidence != null && rejectionReason == "hallucination-rejected" &&
            rawVadHasSpeech && rawVadSpeechMs >= 240 && evidence.NormalizedText.Length >= 2 &&
            evidence.NormalizedText == ClosedSpeechEvidence.Normalize(rejectedText);
        return evidence != null && terminal && currentCapture == requestCapture &&
            requestCapture == evidence.CaptureSessionSerial && requestCapture > 0 &&
            string.IsNullOrWhiteSpace(rawFinalText) &&
            (rejectionReason == "vad-insufficient-speech" || rejectionReason == "recognizer-empty" || agreedShortSpeech) &&
            Finite(now) && now >= evidence.ClosedAt && now - evidence.ClosedAt <= 15f &&
            evidence.NormalizedText.Length > 0 && evidence.NormalizedText == ClosedSpeechEvidence.Normalize(evidence.Text);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
