using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Audio identity uses the WAV hash and integer source samples. ASR revisions are
// separate metadata: their text/windows never become measured singing boundaries.
public static class RecordingSegmentIdentity
{
    public const int Schema = 2;
    public const int MinimumExcludedCharacters = 2, MinimumKeptCharacters = 3;
    public const int MaximumAnchorCharacters = 384;

    public static string TextKey(string text) => new string((text ?? "").Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static bool ValidLegacyAnchors(string exclude, string keep) =>
        exclude != null && keep != null && exclude.Length <= MaximumAnchorCharacters && keep.Length <= MaximumAnchorCharacters &&
        TextKey(exclude).Length >= MinimumExcludedCharacters && TextKey(keep).Length >= MinimumKeptCharacters &&
        !TextKey(exclude).Contains(TextKey(keep)) && !TextKey(keep).Contains(TextKey(exclude));

    public static bool TryWaveFormat(byte[] wav, out int sampleRate, out long sampleCount)
    {
        sampleRate = 0; sampleCount = 0;
        if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return false;
        int format = 0, channels = 0, bits = 0, block = 0; long dataBytes = -1;
        for (long cursor = 12; cursor + 8 <= wav.Length;)
        {
            int at = (int)cursor; uint size = BitConverter.ToUInt32(wav, at + 4);
            if (cursor + 8 + size > wav.Length) return false;
            string kind = Encoding.ASCII.GetString(wav, at, 4);
            if (kind == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(wav, at + 8); channels = BitConverter.ToUInt16(wav, at + 10);
                sampleRate = BitConverter.ToInt32(wav, at + 12); block = BitConverter.ToUInt16(wav, at + 20);
                bits = BitConverter.ToUInt16(wav, at + 22);
            }
            if (kind == "data") dataBytes = size;
            cursor += 8L + size + (size & 1);
        }
        if (format != 1 || channels != 1 || bits != 16 || block != 2 || sampleRate <= 0 || dataBytes <= 0 || dataBytes % block != 0) return false;
        sampleCount = dataBytes / block; return true;
    }

    private static bool Sample(JToken row, string samples, string seconds, int rate, long count, out long value)
    {
        value = 0;
        var integer = row[samples]; var time = row[seconds];
        if (integer != null)
        {
            if (integer.Type != JTokenType.Integer) return false;
            try { value = (long)integer; } catch (Exception) { return false; }
            if (time != null && !Seconds(time, out double t) || time != null && Math.Abs((double)time * rate - value) > .501) return false;
        }
        else
        {
            if (!Seconds(time, out double t)) return false;
            double position = Math.Round(t * rate, MidpointRounding.AwayFromZero);
            if (position < 0 || position > count) return false;
            value = (long)position;
        }
        return value >= 0 && value <= count;
    }

    private static bool Seconds(JToken t, out double value)
    {
        value = double.NaN;
        if (t == null || t.Type != JTokenType.Float && t.Type != JTokenType.Integer) return false;
        value = (double)t; return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public static bool TryCanonicalSegments(JArray segments, string audioHash, int sampleRate, long sampleCount,
        out JArray canonical, out string reason)
    {
        canonical = new JArray(); reason = "segment-evidence-invalid";
        if (segments == null || string.IsNullOrEmpty(audioHash) || sampleRate <= 0 || sampleCount <= 0) return false;
        foreach (var token in segments)
        {
            if (!(token is JObject row) || row["text"]?.Type != JTokenType.String ||
                (string)row["status"] != "ok" ||
                !Sample(row, "start_sample", "start_seconds", sampleRate, sampleCount, out long start) ||
                !Sample(row, "end_sample", "end_seconds", sampleRate, sampleCount, out long end) || end <= start) return false;
            string text = (string)row["text"], origin = (string)row["origin"] ?? "segment", region = (string)row["region"] ?? "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (row["segment_id"] != null && ((string)row["kind"] != "asr_window" ||
                (string)row["boundary_status"] != "candidate" || row["word_aligned"]?.Type != JTokenType.Boolean || (bool)row["word_aligned"]))
                return false;
            var identity = new JObject { ["audio_sha256"] = audioHash, ["sample_rate"] = sampleRate,
                ["start_sample"] = start, ["end_sample"] = end, ["text"] = text, ["origin"] = origin, ["region"] = region };
            string id = "seg_" + RecordingBoundarySource.Hash(Encoding.UTF8.GetBytes(identity.ToString(Formatting.None)));
            if (row["segment_id"] != null && (string)row["segment_id"] != id) { reason = "segment-evidence-tampered"; return false; }
            var normalized = new JObject { ["segment_id"] = id, ["id"] = row["id"]?.DeepClone(),
                ["start_sample"] = start, ["end_sample"] = end,
                ["start_seconds"] = start / (double)sampleRate, ["end_seconds"] = end / (double)sampleRate,
                ["text"] = text, ["origin"] = origin, ["region"] = region, ["status"] = "ok",
                ["kind"] = "asr_window", ["boundary_status"] = "candidate", ["word_aligned"] = false };
            if (canonical.OfType<JObject>().Any(s => (string)s["segment_id"] == id)) continue;
            canonical.Add(normalized);
        }
        reason = null; return true;
    }

    public static JObject CreateEvidence(string audioHash, int sampleRate, long sampleCount, string recordingId,
        int capture, JArray segments)
    {
        if (!TryCanonicalSegments(segments, audioHash, sampleRate, sampleCount, out var canonical, out _)) return null;
        return new JObject { ["schema"] = Schema, ["audio_sha256"] = audioHash, ["sample_rate"] = sampleRate,
            ["sample_count"] = sampleCount, ["recordingId"] = recordingId, ["capture"] = capture,
            ["evidence_revision"] = Revision(canonical), ["segments"] = canonical,
            ["scope"] = "Immutable PCM identity and ASR observation windows. Segment IDs select semantic evidence; no singing classification, word alignment, crop approval or playback permission." };
    }

    public static string Revision(JArray canonical) => RecordingBoundarySource.Hash(Encoding.UTF8.GetBytes(
        new JArray(canonical.OfType<JObject>().Select(s => s["segment_id"]?.DeepClone())).ToString(Formatting.None)));

    public static bool Matches(JObject evidence, string audioHash, int rate, long samples, JArray sourceSegments)
    {
        try { return MatchesCore(evidence, audioHash, rate, samples, sourceSegments); }
        catch (Exception e) when (e is InvalidCastException || e is FormatException || e is OverflowException || e is ArgumentException) { return false; }
    }
    private static bool MatchesCore(JObject evidence, string audioHash, int rate, long samples, JArray sourceSegments)
    {
        if ((string)evidence?["audio_sha256"] != audioHash || !(evidence?["segments"] is JArray proposed)) return false;
        if ((int?)evidence["schema"] == Schema && ((int?)evidence["sample_rate"] != rate || (long?)evidence["sample_count"] != samples)) return false;
        if (!TryCanonicalSegments(proposed, audioHash, rate, samples, out var left, out _) ||
            !TryCanonicalSegments(sourceSegments, audioHash, rate, samples, out var right, out _)) return false;
        return Revision(left) == Revision(right) &&
            (evidence["evidence_revision"] == null || (string)evidence["evidence_revision"] == Revision(left));
    }

    public static bool TrySelect(JObject edit, JObject evidence, out JObject selection, out string reason)
    {
        try { return TrySelectCore(edit, evidence, out selection, out reason); }
        catch (Exception e) when (e is InvalidCastException || e is FormatException || e is OverflowException || e is ArgumentException)
        { selection = null; reason = "segment-selection-invalid"; return false; }
    }
    private static bool TrySelectCore(JObject edit, JObject evidence, out JObject selection, out string reason)
    {
        selection = null; reason = "segment-selection-invalid";
        if (edit == null || edit.Count != 5 || (string)edit["kind"] != "segments" || (string)edit["target"] != "material" ||
            (string)edit["evidence_revision"] != (string)evidence?["evidence_revision"] ||
            !(edit["keep_segment_ids"] is JArray keep) || !(edit["exclude_segment_ids"] is JArray exclude) ||
            keep.Count == 0 || keep.Count > 32 || exclude.Count > 32 ||
            keep.Concat(exclude).Any(id => id.Type != JTokenType.String) ||
            keep.Concat(exclude).Select(id => (string)id).Distinct().Count() != keep.Count + exclude.Count) return false;
        if ((int?)evidence?["schema"] != Schema || !(evidence["segments"] is JArray segments) ||
            !Matches(evidence, (string)evidence["audio_sha256"], (int?)evidence["sample_rate"] ?? 0,
                (long?)evidence["sample_count"] ?? 0, segments)) return false;
        var ids = keep.Concat(exclude).Values<string>().ToArray();
        if (ids.Any(id => !segments.OfType<JObject>().Any(s => (string)s["segment_id"] == id))) { reason = "segment-selection-unknown-source"; return false; }
        selection = new JObject { ["schema"] = 1, ["audio_sha256"] = evidence["audio_sha256"].DeepClone(),
            ["sample_rate"] = evidence["sample_rate"].DeepClone(), ["sample_count"] = evidence["sample_count"].DeepClone(),
            ["evidence_revision"] = evidence["evidence_revision"].DeepClone(),
            ["keep"] = new JArray(segments.OfType<JObject>().Where(s => keep.Values<string>().Contains((string)s["segment_id"])).Select(s => s.DeepClone())),
            ["exclude"] = new JArray(segments.OfType<JObject>().Where(s => exclude.Values<string>().Contains((string)s["segment_id"])).Select(s => s.DeepClone())),
            ["status"] = "candidate", ["scope"] = "Read-only semantic selection; requires an independently validated acoustic boundary provider before cropping." };
        reason = null; return true;
    }
}
