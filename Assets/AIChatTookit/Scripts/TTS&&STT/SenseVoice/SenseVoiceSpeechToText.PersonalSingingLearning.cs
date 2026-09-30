using Newtonsoft.Json.Linq;

public partial class SenseVoiceSpeechToText
{
    // Read recorder geometry before continuation isolation or singing crops.
    public JObject DescribePersonalLearningPcm(double originalSeconds) =>
        DescribeRecordingPcmLineage(m_LiveRecordingCandidateSessionSerial, originalSeconds);
}
