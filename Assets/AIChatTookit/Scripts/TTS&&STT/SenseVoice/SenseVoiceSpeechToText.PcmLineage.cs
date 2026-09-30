using Newtonsoft.Json.Linq;

public partial class SenseVoiceSpeechToText
{
    private readonly RecordingPcmLineage m_RecordingPcmLineage = new RecordingPcmLineage();

    private JObject DescribeRecordingPcmLineage(int capture, double seconds) =>
        m_RecordingPcmLineage.Describe(capture, seconds);

    private void RegisterRecordingPcmLineage(byte[] audio, int capture)
    {
        var lineage = DescribeRecordingPcmLineage(capture, WaveformTrace.Duration(audio));
        WaveformTrace.RegisterInputLineage(audio, lineage);
    }
}
