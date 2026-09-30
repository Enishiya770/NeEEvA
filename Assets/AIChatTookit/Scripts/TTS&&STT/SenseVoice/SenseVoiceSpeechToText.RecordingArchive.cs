using UnityEngine;

public partial class SenseVoiceSpeechToText
{
    private static RetainedRecordingPcm RetainPcm(byte[] bytes)
    {
        if (bytes == null) return null;
        var provenance = WaveformTrace.Describe(bytes);
        RetainedRecordingPcmStore.Shared.OnError = error => Debug.LogWarning("[Recording/Archive] " + error);
        var retained = RetainedRecordingPcmStore.Shared.Put(bytes);
        double seconds = WaveformTrace.Duration(bytes);
        retained.DurationSeconds = double.IsNaN(seconds) ? 0 : seconds;
        // Registration can follow the assignment; snapshot the final mapping at
        // eviction, then restore it only after the disk payload passes its hash.
        WaveformTrace.ObserveRetainedMapping(bytes, value => provenance = value);
        retained.BeforeEvict = value => provenance = WaveformTrace.Describe(value);
        retained.Restore = value => WaveformTrace.RestoreRetainedMapping(value, provenance);
        return retained;
    }
}
