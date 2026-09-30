using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Recorder-owned copy geometry, independent of ASR text, speaker verdicts and
// preparation state. A copied prefix keeps its original source coordinates.
public sealed class RecordingPcmLineage
{
    private sealed class Capture
    {
        public int Serial;
        public string Source;
        public Capture Parent;
        public double Prefix;
    }
    private readonly Dictionary<int, Capture> captures = new Dictionary<int, Capture>();
    private readonly string session = Guid.NewGuid().ToString("N");
    private int incarnation;

    private Capture Get(int serial)
    {
        if (!captures.TryGetValue(serial, out var capture))
        {
            capture = new Capture { Serial = serial, Source = "pcm:" + session + ":" + (++incarnation) };
            captures.Add(serial, capture);
        }
        return capture;
    }

    public void Bind(int serial, int earlierSerial, double prefixSeconds)
    {
        if (earlierSerial <= 0 || serial <= earlierSerial || !Finite(prefixSeconds) || prefixSeconds <= 0) return;
        var capture = Get(serial);
        // The recorder binds once at capture start. Do not rewrite a published
        // ancestry if a duplicate or stale caller later supplies different data.
        if (capture.Parent != null) return;
        capture.Parent = Get(earlierSerial); capture.Prefix = prefixSeconds;
    }

    public void Retire(int serial)
    {
        // Descendants keep their immutable parent object. A reanalysed suffix
        // gets a new source identity, never the retired parent's old identity.
        if (serial > 0) captures.Remove(serial);
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    public JObject Describe(int serial, double seconds)
    {
        var regions = new JArray();
        bool known = serial > 0 && Finite(seconds) && seconds > 0;
        if (known) Append(Get(serial), 0, seconds, regions);
        return new JObject { ["known"] = known, ["basis"] = "recorder-prefix-copy",
            ["captureSerial"] = serial, ["regions"] = regions };
    }

    private static void Append(Capture capture, double start, double end, JArray regions)
    {
        if (capture.Parent != null && start < capture.Prefix)
            Append(capture.Parent, start, Math.Min(end, capture.Prefix), regions);
        double ownStart = capture.Parent == null ? start : Math.Max(start, capture.Prefix);
        if (end <= ownStart) return;
        regions.Add(new JObject { ["sourceId"] = capture.Source, ["sourceCaptureSerial"] = capture.Serial,
            ["sourceStartSeconds"] = ownStart, ["sourceEndSeconds"] = end,
            ["rawStartSeconds"] = ownStart, ["rawEndSeconds"] = end });
    }
}
