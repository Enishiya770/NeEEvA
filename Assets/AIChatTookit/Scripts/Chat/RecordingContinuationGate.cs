using System;

/// <summary>
/// A negative, fresh probe may defer only the RMS fallback that would copy an
/// already finalized, unheard capture. This is not transcript deduplication.
/// </summary>
public static class RecordingContinuationGate
{
    public sealed class Evidence
    {
        public int CurrentCapture, RetainedCapture, FinalCapture;
        public bool HasRetainedAudio, SameMicrophone, Replied, RejectedSelfSpeech, BargeIn;
        public string FinalSource, FinalText;
        public bool VadAvailable, Speech, Tonal;
        public int SpeechMs;
        public float Now, ClosedAt, ProbeEndedAt, ProbeSeconds;
    }

    public static bool ShouldDeferRmsFallback(Evidence value)
    {
        if (value == null || value.BargeIn || value.Replied || value.RejectedSelfSpeech ||
            !value.HasRetainedAudio || !value.SameMicrophone || value.CurrentCapture <= 0 ||
            value.CurrentCapture != value.RetainedCapture || value.FinalCapture != value.RetainedCapture ||
            string.IsNullOrWhiteSpace(value.FinalText) ||
            (value.FinalSource != "full-asr" && value.FinalSource != "full-preview" &&
             value.FinalSource != "closed-stream-recovery")) return false;
        if (!value.VadAvailable || value.Speech || value.Tonal || value.SpeechMs != 0) return false;
        if (!Finite(value.Now) || !Finite(value.ClosedAt) || !Finite(value.ProbeEndedAt) ||
            !Finite(value.ProbeSeconds) || value.ProbeSeconds <= 0f) return false;
        float gap = value.Now - value.ClosedAt;
        float probeAge = value.Now - value.ProbeEndedAt;
        // The complete probe must concern new sound after the old close. A
        // delayed negative result or one spanning old speech cannot veto onset.
        return gap >= 0f && gap <= 3f && probeAge >= 0f && probeAge <= .75f &&
            value.ProbeEndedAt - value.ProbeSeconds >= value.ClosedAt;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
