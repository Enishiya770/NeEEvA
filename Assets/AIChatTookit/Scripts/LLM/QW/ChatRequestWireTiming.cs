using System;
using Newtonsoft.Json.Linq;

/// <summary>One HTTP request's monotonic telemetry. No model text, prompts or credentials.</summary>
public sealed class ChatRequestWireTiming
{
    private readonly string id = Guid.NewGuid().ToString("N"), kind;
    private readonly int generation;
    private double? sent, bytes, content, speech, completed, sentRealtime;
    private string sentUtc;
    public ChatRequestWireTiming(int requestGeneration, string requestKind)
    { generation = requestGeneration; kind = requestKind; }
    private static bool Valid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
    public void MarkSent(double seconds, double realtime, string utc)
    {
        if (sent.HasValue || !Valid(seconds) || !Valid(realtime)) return;
        sent = seconds; sentRealtime = realtime; sentUtc = utc;
    }
    private void First(ref double? field, double seconds)
    { if (sent.HasValue && !field.HasValue && Valid(seconds) && seconds >= sent.Value) field = seconds; }
    public void MarkBytes(double seconds) => First(ref bytes, seconds);
    public void MarkContent(double seconds) => First(ref content, seconds);
    public void MarkSpeech(double seconds) => First(ref speech, seconds);
    public void MarkComplete(double seconds) => First(ref completed, seconds);
    private double? SinceSend(double? value) => sent.HasValue && value.HasValue ? value.Value - sent.Value : (double?)null;
    public JObject Snapshot(string state, long? httpStatus, string transportResult) => new JObject {
        ["requestId"] = id, ["requestGeneration"] = generation, ["requestKind"] = kind,
        ["state"] = state, ["httpStatus"] = httpStatus, ["transportResult"] = transportResult,
        ["httpSentAtUtc"] = sentUtc, ["httpSentRealtimeSeconds"] = sentRealtime,
        ["preSendPreparationSeconds"] = sent,
        ["httpToFirstBytesSeconds"] = SinceSend(bytes),
        ["httpToFirstContentSeconds"] = SinceSend(content),
        ["httpToFirstSpeechDeltaSeconds"] = SinceSend(speech),
        ["httpToCompletionSeconds"] = SinceSend(completed),
        ["scope"] = "Actual HTTP SendWebRequest boundary, separate from capture/planning and EOU. Bytes may be headers/events without content. Speech delta is decoded candidate text, not approved speech or TTS playback. Missing values are unknown, not zero. Superseded requests cannot authorize callbacks or actions."
    };
}
