using UnityEngine;

public partial class RTSpeechHandler
{
    private readonly PersonalSingingLearningRecorder personalLearningRecorder = new PersonalSingingLearningRecorder();

    private void SealPersonalLearningCapture(AudioClip clip, bool realtime)
    {
        var lineage = realtime && clip != null && m_NeuralVadClient != null
            ? m_NeuralVadClient.DescribePersonalLearningPcm(clip.samples / (double)clip.frequency)
            : null;
        personalLearningRecorder.Seal(clip, lineage);
    }
}
