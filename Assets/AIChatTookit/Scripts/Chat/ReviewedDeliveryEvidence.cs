using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Share a completed semantic check, never a draft or a new action authority.
public sealed class ReviewedDeliveryEvidence
{
    private sealed class Entry
    {
        public int Epoch, Generation;
        public JObject Result;
        public WorkResultDeliveryProtocol.Report Report;
    }
    private readonly List<Entry> entries = new List<Entry>();

    public void Remember(int epoch, int generation, JObject delivery, WorkResultDeliveryProtocol.Report[] reports)
    {
        entries.RemoveAll(e => e.Epoch != epoch || e.Generation != generation);
        foreach (var report in reports ?? new WorkResultDeliveryProtocol.Report[0]) {
            var result = (delivery?["results"] as JArray)?.OfType<JObject>().FirstOrDefault(r => (string)r["id"] == report?.id);
            if (WorkResultDeliveryProtocol.CoverageRejection(result, report, (string)result?["completedPlaybackText"]) != null) continue;
            entries.RemoveAll(e => (string)e.Result["id"] == report.id);
            entries.Add(new Entry { Epoch = epoch, Generation = generation, Result = (JObject)result.DeepClone(),
                Report = JToken.FromObject(report).ToObject<WorkResultDeliveryProtocol.Report>() });
        }
        if (entries.Count > 12) entries.RemoveRange(0, entries.Count - 12);
    }

    public WorkResultDeliveryProtocol.Report Take(int epoch, int generation, JObject result)
    {
        entries.RemoveAll(e => e.Epoch != epoch || e.Generation != generation);
        var entry = entries.FirstOrDefault(e => (string)e.Result["id"] == (string)result?["id"]);
        if (entry == null) return null;
        entries.Remove(entry);
        // Later audio can retract an earlier result; only the exact checked
        // window and receipt transfer. Unknown extensions remain compared.
        foreach (string field in new[] { "id", "source", "status", "facts", "taskContext", "playbackRevision",
            "completedPlaybackText", "completedAudio", "latestCorrectionStart" })
            if (!ActivityEvidencePolicy.Equal(entry.Result[field], result[field])) return null;
        return WorkResultDeliveryProtocol.CoverageRejection(result, entry.Report, (string)result["completedPlaybackText"]) == null
            ? entry.Report : null;
    }
}
