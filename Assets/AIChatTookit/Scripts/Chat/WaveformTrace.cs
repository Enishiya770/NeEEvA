using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

/// <summary>Read-only provenance of actual PCM buffers. Missing/ambiguous origins remain unknown.</summary>
public static class WaveformTrace
{
    private sealed class Entry { public JObject Value; }
    private sealed class RetainedObservers { public readonly List<Action<JObject>> Values = new List<Action<JObject>>(); }
    private static readonly ConditionalWeakTable<byte[], RetainedObservers> Observers = new ConditionalWeakTable<byte[], RetainedObservers>();
    private static readonly ConditionalWeakTable<byte[], Entry> ByObject = new ConditionalWeakTable<byte[], Entry>();
    private static readonly Dictionary<string, JObject> ByDigest = new Dictionary<string, JObject>();
    private static readonly Queue<string> Order = new Queue<string>();
    private static readonly object Gate = new object();
    private const int Capacity = 128, MaximumRegions = 128;
    private static string Digest(byte[] bytes)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static bool Header(byte[] b, out int rate, out int channels, out int bits, out long dataBytes)
    {
        rate = channels = bits = 0; dataBytes = 0;
        if (b == null || b.Length < 44 || BitConverter.ToUInt32(b, 0) != 0x46464952 || BitConverter.ToUInt32(b, 8) != 0x45564157) return false;
        for (long at = 12; at + 8 <= b.Length; )
        {
            int i = (int)at; uint size = BitConverter.ToUInt32(b, i + 4);
            if (at + 8 + size > b.Length) return false;
            uint name = BitConverter.ToUInt32(b, i);
            if (name == 0x20746d66 && size >= 16)
            {
                if (BitConverter.ToUInt16(b, i + 8) != 1) return false;
                channels = BitConverter.ToUInt16(b, i + 10); rate = BitConverter.ToInt32(b, i + 12);
                bits = BitConverter.ToUInt16(b, i + 22);
            }
            if (name == 0x61746164) dataBytes += size;
            at += 8L + size + (size & 1);
        }
        return rate > 0 && channels > 0 && (bits == 8 || bits == 16 || bits == 24 || bits == 32) && dataBytes > 0;
    }
    public static double Duration(byte[] bytes)
    {
        try { return Header(bytes, out int r, out int c, out int b, out long n) ? n / (double)(r * c * (b / 8)) : double.NaN; }
        catch { return double.NaN; }
    }
    private static JObject Find(byte[] bytes)
    {
        if (bytes == null) return null;
        lock (Gate)
        {
            if (ByDigest.Count == 0) return null;
            if (ByObject.TryGetValue(bytes, out var e)) return (JObject)e.Value.DeepClone();
            return ByDigest.TryGetValue(Digest(bytes), out var v) ? (JObject)v?.DeepClone() : null;
        }
    }
    private static void Store(byte[] bytes, JObject value)
    {
        if (bytes == null || value == null) return;
        lock (Gate)
        {
            ByObject.Remove(bytes); ByObject.Add(bytes, new Entry { Value = (JObject)value.DeepClone() });
            string id = Digest(bytes);
            if (Observers.TryGetValue(bytes, out var observers))
                foreach (var receive in observers.Values) {
                    var retained = (JObject)value.DeepClone(); retained["sha256"] = id;
                    try { receive(retained); } catch { /* Diagnostic retention must not alter PCM registration. */ }
                }
            if (ByDigest.TryGetValue(id, out var existing))
            {
                // Identical bytes with different recording identities cannot resolve a clone's ownership.
                if (!JToken.DeepEquals(existing, value)) ByDigest[id] = null;
                return;
            }
            ByDigest[id] = (JObject)value.DeepClone(); Order.Enqueue(id);
            while (Order.Count > Capacity) ByDigest.Remove(Order.Dequeue());
        }
    }
    // A weak-key subscription captures later registration without retaining the
    // audio buffer. This also covers a single WAV too large for the PCM cache.
    internal static void ObserveRetainedMapping(byte[] bytes, Action<JObject> receive)
    {
        if (bytes == null || receive == null) return;
        lock (Gate) Observers.GetValue(bytes, _ => new RetainedObservers()).Values.Add(receive);
    }

    // Only the archive that retained these exact bytes may restore this detached
    // mapping. A digest match binds provenance to PCM; text or clip aliases cannot.
    internal static bool RestoreRetainedMapping(byte[] bytes, JObject retained)
    {
        if (bytes == null || retained == null || (string)retained["sha256"] != Digest(bytes)) return false;
        if (!(retained["regions"] is JArray regions) || retained["mappingComplete"]?.Type != JTokenType.Boolean) return false;
        Store(bytes, new JObject { ["mappingComplete"] = retained["mappingComplete"].DeepClone(),
            ["regions"] = regions.DeepClone() });
        return true;
    }

    public static void RegisterInput(byte[] bytes, int captureSerial)
    {
        try
        {
            double duration = Duration(bytes); if (double.IsNaN(duration)) return;
            Store(bytes, new JObject { ["mappingComplete"] = true, ["regions"] = new JArray(new JObject {
                ["captureSerial"] = captureSerial, ["sourceSha256"] = Digest(bytes),
                ["sourceStartSeconds"] = 0, ["sourceEndSeconds"] = duration,
                ["outputStartSeconds"] = 0, ["outputEndSeconds"] = duration, ["kind"] = "recording" }) });
        }
        catch { }
    }
    public static void RegisterInputLineage(byte[] bytes, JObject lineage)
    {
        try
        {
            if ((bool?)lineage?["known"] != true || !(lineage["regions"] is JArray source)) return;
            var regions = new JArray();
            foreach (JObject r in source)
                regions.Add(new JObject { ["captureSerial"] = r["sourceCaptureSerial"], ["sourceId"] = r["sourceId"],
                    ["sourceStartSeconds"] = r["sourceStartSeconds"], ["sourceEndSeconds"] = r["sourceEndSeconds"],
                    ["outputStartSeconds"] = r["rawStartSeconds"], ["outputEndSeconds"] = r["rawEndSeconds"],
                    ["kind"] = "recording" });
            Store(bytes, new JObject { ["mappingComplete"] = true, ["regions"] = regions });
        }
        catch { }
    }
    public static bool SharesCopiedPcm(byte[] left, double leftStart, double leftEnd,
        byte[] right, double rightStart, double rightEnd)
    {
        try
        {
            var a = Find(left); var b = Find(right);
            if ((bool?)a?["mappingComplete"] != true || (bool?)b?["mappingComplete"] != true) return false;
            bool Project(JObject r, double start, double end, out double from, out double to)
            {
                from = to = 0;
                if (r["sourceId"]?.Type != JTokenType.String || r["sourceStartSeconds"] == null) return false;
                double x = (double)r["outputStartSeconds"], y = (double)r["outputEndSeconds"];
                double lo = Math.Max(start, x), hi = Math.Min(end, y);
                if (hi <= lo || y <= x) return false;
                double sourceStart = (double)r["sourceStartSeconds"], sourceEnd = (double)r["sourceEndSeconds"];
                from = sourceStart + (lo - x) * (sourceEnd - sourceStart) / (y - x);
                to = sourceStart + (hi - x) * (sourceEnd - sourceStart) / (y - x); return true;
            }
            foreach (JObject x in (JArray)a["regions"])
                foreach (JObject y in (JArray)b["regions"])
                    if ((string)x["sourceId"] == (string)y["sourceId"] &&
                        Project(x, leftStart, leftEnd, out double ax, out double ay) &&
                        Project(y, rightStart, rightEnd, out double bx, out double by) &&
                        Math.Min(ay, by) - Math.Max(ax, bx) > .001) return true;
        }
        catch { }
        return false;
    }
    // Range on the ACTUAL source buffer; resampling is represented by its measured output duration.
    public static void Transform(byte[] source, byte[] output, double start, double end)
    {
        try
        {
            var origin = Find(source); double duration = Duration(output);
            if (origin == null || double.IsNaN(duration) || double.IsNaN(start) || double.IsNaN(end) || end <= start) return;
            var regions = new JArray();
            foreach (JObject r in (JArray)origin["regions"])
            {
                double a = (double)r["outputStartSeconds"], b = (double)r["outputEndSeconds"];
                double lo = Math.Max(start, a), hi = Math.Min(end, b);
                if (hi <= lo || b <= a) continue;
                var next = (JObject)r.DeepClone();
                if (r["sourceStartSeconds"] != null)
                {
                    double x = (double)r["sourceStartSeconds"], y = (double)r["sourceEndSeconds"];
                    next["sourceStartSeconds"] = x + (y - x) * (lo - a) / (b - a);
                    next["sourceEndSeconds"] = x + (y - x) * (hi - a) / (b - a);
                }
                next["outputStartSeconds"] = (lo - start) / (end - start) * duration;
                next["outputEndSeconds"] = (hi - start) / (end - start) * duration;
                regions.Add(next);
            }
            Store(output, new JObject { ["mappingComplete"] = (bool)origin["mappingComplete"] &&
                start >= 0 && end <= Duration(source) + 0.0001, ["regions"] = regions });
        }
        catch { }
    }
    public static void Annotate(byte[] bytes, string clipRef, int revision)
    {
        try
        {
            var value = Find(bytes); if (value == null) return;
            foreach (JObject r in (JArray)value["regions"]) { r["clipRef"] = clipRef; r["revision"] = revision; }
            // Annotation belongs to this material object. Keep the digest's unannotated
            // timing lineage usable for clones without guessing which material owns them.
            lock (Gate)
            {
                ByObject.Remove(bytes);
                ByObject.Add(bytes, new Entry { Value = (JObject)value.DeepClone() });
            }
        }
        catch { }
    }
    public static void Concatenate(byte[] output, IList<byte[]> pieces, IList<float> gaps)
    {
        try
        {
            lock (Gate) { if (ByDigest.Count == 0) return; }
            var regions = new JArray(); double cursor = 0; bool complete = true;
            Header(output, out int rate, out _, out _, out _);
            for (int i = 0; i < pieces.Count; i++)
            {
                double gap = rate > 0 ? Math.Round(gaps[i] * rate) / rate : gaps[i];
                if (gap > 0) regions.Add(new JObject { ["kind"] = "inserted-silence", ["outputStartSeconds"] = cursor, ["outputEndSeconds"] = cursor + gap });
                cursor += gap;
                var value = Find(pieces[i]); double duration = Duration(pieces[i]);
                if (double.IsNaN(duration)) return;
                if (value == null) { complete = false; regions.Add(new JObject { ["kind"] = "unknown", ["outputStartSeconds"] = cursor, ["outputEndSeconds"] = cursor + duration }); }
                else
                {
                    complete &= (bool)value["mappingComplete"];
                    foreach (JObject r in (JArray)value["regions"])
                    { var next = (JObject)r.DeepClone(); next["outputStartSeconds"] = cursor + (double)r["outputStartSeconds"]; next["outputEndSeconds"] = cursor + (double)r["outputEndSeconds"]; regions.Add(next); }
                }
                cursor += duration;
                if (regions.Count > MaximumRegions) return;
            }
            Store(output, new JObject { ["mappingComplete"] = complete && Math.Abs(cursor - Duration(output)) < .001, ["regions"] = regions });
        }
        catch { }
    }
    public static JObject Describe(byte[] bytes, string requestId = null)
    {
        try
        {
            var value = Find(bytes) ?? new JObject { ["mappingComplete"] = false, ["regions"] = new JArray() };
            bool valid = Header(bytes, out int rate, out int channels, out int bits, out long data);
            value["requestId"] = requestId; value["sha256"] = bytes == null ? null : Digest(bytes);
            value["bytes"] = bytes?.Length ?? 0; value["sampleRate"] = valid ? (int?)rate : null;
            value["channels"] = valid ? (int?)channels : null; value["bits"] = valid ? (int?)bits : null;
            value["durationSeconds"] = valid ? (double?)Duration(bytes) : null;
            value["scope"] = "actual-upload-buffer; source coordinates are retained PCM lineage, not verified lyric boundaries; missing mapping is unknown";
            return value;
        }
        catch { return new JObject { ["mappingComplete"] = false, ["error"] = "diagnostic-unavailable" }; }
    }
}
