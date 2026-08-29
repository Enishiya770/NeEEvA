using UnityEngine;

/// <summary>
/// Forwards samples from the streamed singing secondary AudioSource to the character's existing
/// lip-sync and echo-reference processors. The relay lives on a dedicated child object, so Unity
/// sees exactly one AudioSource for every OnAudioFilterRead component.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class PlaybackAudioFilterRelay : MonoBehaviour
{
    private Audio2LipScript m_LipSync;
    private PlaybackEchoReferenceTap m_EchoReference;

    public void Configure(Audio2LipScript lipSync, PlaybackEchoReferenceTap echoReference)
    {
        m_LipSync = lipSync;
        m_EchoReference = echoReference;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (m_LipSync != null) m_LipSync.ProcessAudioSamplesRaw(data, channels);
        if (m_EchoReference != null) m_EchoReference.ProcessAudioSamplesRaw(data, channels);
    }
}
