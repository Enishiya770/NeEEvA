using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Contract between source-bound semantic selection and an accepted acoustic
// provider. Candidate SED scores/ASR windows alone cannot create a crop lease.
public static class RecordingSegmentBoundary
{
    public static JObject Request(RecordingBoundarySource source, JObject selection) => new JObject {
        ["schema"] = 1, ["request_id"] = "segment-boundary-" + Guid.NewGuid().ToString("N"),
        ["source_id"] = source.Clip, ["source_revision"] = source.Revision,
        ["audio_sha256"] = source.RawHash, ["sample_rate"] = source.SampleRate, ["sample_count"] = source.SampleCount,
        ["pcm_lineage_hash"] = source.LineageHash ?? "",
        ["evidence_revision"] = source.EvidenceRevision, ["selection"] = selection.DeepClone(),
        ["selection_sha256"] = SelectionHash(selection),
        ["analysis_start_sample"] = Sample(source.Start, source.SampleRate),
        ["analysis_end_sample"] = Sample(source.End, source.SampleRate),
        ["scope"] = "Read-only acoustic boundary request. ASR IDs identify desired content, not verified audio cuts." };

    public static string SelectionHash(JObject selection) => RecordingBoundarySource.Hash(Encoding.UTF8.GetBytes(selection.ToString(Formatting.None)));
    public static long Sample(double seconds, int rate) => (long)Math.Round(seconds * rate, MidpointRounding.AwayFromZero);

    public static bool TryValidate(RecordingBoundarySource source, JObject request, JObject result,
        out long start, out long end, out string reason)
    {
        try { return TryValidateCore(source, request, result, out start, out end, out reason); }
        catch (Exception e) when (e is InvalidCastException || e is FormatException || e is OverflowException || e is ArgumentException)
        { start = end = 0; reason = "boundary_result_binding_mismatch"; return false; }
    }

    private static bool TryValidateCore(RecordingBoundarySource source, JObject request, JObject result,
        out long start, out long end, out string reason)
    {
        start = end = 0; reason = "segment-boundary-unverified";
        if (source == null || request == null || (string)result?["status"] != "resolved" || (bool?)result["classification_supported"] != true ||
            (bool?)result["boundary_supported"] != true || (string)result["quality"] != "classification-and-refinement-supported" ||
            string.IsNullOrWhiteSpace((string)result["provider"]) || string.IsNullOrWhiteSpace((string)result["model_version"]) ||
            string.IsNullOrWhiteSpace((string)result["refinement_version"])) return false;
        reason = "boundary_result_binding_mismatch";
        if ((string)result["request_id"] != (string)request["request_id"] || (string)result["source_id"] != source.Clip ||
            (string)result["source_revision"] != source.Revision || (string)result["audio_sha256"] != source.RawHash ||
            (string)result["pcm_lineage_hash"] != (source.LineageHash ?? "") ||
            (int?)result["sample_rate"] != source.SampleRate || (long?)result["sample_count"] != source.SampleCount ||
            (string)result["evidence_revision"] != source.EvidenceRevision ||
            (string)result["selection_sha256"] != (string)request["selection_sha256"] ||
            !(result["range"] is JObject window) || window["start_sample"]?.Type != JTokenType.Integer || window["end_sample"]?.Type != JTokenType.Integer)
            return false;
        start = (long)window["start_sample"]; end = (long)window["end_sample"];
        long lo = (long)request["analysis_start_sample"], hi = (long)request["analysis_end_sample"];
        if (start < lo || end > hi || start < 0 || end > source.SampleCount || end - start < source.SampleRate) return false;
        if (!(result["uncertainty"] is JObject uncertainty)) return false;
        string[] fields = { "start_min_sample", "start_max_sample", "end_min_sample", "end_max_sample" };
        if (fields.Any(f => uncertainty[f]?.Type != JTokenType.Integer)) return false;
        long startMin = (long)uncertainty[fields[0]], startMax = (long)uncertainty[fields[1]],
            endMin = (long)uncertainty[fields[2]], endMax = (long)uncertainty[fields[3]];
        if (startMin < lo || startMin > start || startMax < start || endMin > end || endMax < end || endMax > hi || startMax >= endMin) return false;
        reason = null; return true;
    }
}
