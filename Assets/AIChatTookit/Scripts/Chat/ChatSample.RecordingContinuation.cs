using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Only final audio callbacks enter this gate. Direct text input never inherits microphone provenance.
    private void DealingRecordingFinalCallback(string text, bool streamingExitAtSubmission)
    {
        if (!string.IsNullOrWhiteSpace(text) && RetainUnconfirmedInheritedFinal(text)) return;
        DealingTextCallback(text, streamingExitAtSubmission);
    }

    private bool RetainUnconfirmedInheritedFinal(string callbackText)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null || !sense.MatchesFinalTranscriptCallback(sense.LastFinalCaptureSerial, callbackText) ||
            !AcceptedRecordingInputCurrent ||
            !ReferenceEquals(sense, m_AcceptedRecordingInputProvider) ||
            m_RTSpeechHandler == null || m_RTSpeechHandler.ChatOwner != this ||
            !m_RTSpeechHandler.IsUnconfirmedInheritedFinal(
                (int?)m_AcceptedRecordingIdentity?["capture"] ?? 0,
                sense.LastFinalCaptureSerial, sense.LastFinalRawText)) return false;

        // Keep the complete new recording as evidence. Do not transform a copied
        // old utterance into new authority or reset its still-running response.
        RetainCompletedRecordingEvidence(sense);
        m_RealtimeUserTurnPending = false;
        m_AcceptedRecordingInputTrace?.Mark("input.inherited_final_retained", new JObject {
            ["acceptedCapture"] = m_AcceptedRecordingIdentity["capture"].DeepClone(),
            ["candidateCapture"] = sense.LastFinalCaptureSerial,
            ["scope"] = "Same exact transcript from copied accepted PCM, without confirmed new tail voice; audio retained, no new user instruction"
        });
        return true;
    }
}
