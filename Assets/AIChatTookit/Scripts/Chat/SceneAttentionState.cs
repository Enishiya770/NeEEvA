using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Persistent salience, not perception or execution authority. All entries originate
// in retained observations; the source memory and task ledger remain authoritative.
public sealed class SceneAttentionState
{
    private sealed class Entry
    {
        public string id, capture, description, descriptionCapture;
        public double observed, descriptionObserved = -1, selectedAt;
    }
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
    private string owner, signature, mode = "idle", reason = "idle", query = "";
    private double settledAt, lastChange;
    private int revision;
    public double UpdateInterval => mode == "task" || mode == "investigate" ? .2 : mode == "dialogue" ? .5 : 2;

    public void Update(string binding, string purpose, string text, bool active, bool uncertain,
        bool conversing, IEnumerable<JObject> observations, IEnumerable<string> required, double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return;
        if (binding != owner) { entries.Clear(); selected.Clear(); owner = binding; signature = null; revision++; }
        string desired = uncertain ? "investigate" : active ? "task" : conversing ? "dialogue" : "idle";
        string next = purpose + "|" + desired;
        // A new purpose, active task, or failure preempts immediately. Quiet decay
        // has a short hold, so transient frame changes do not churn attention.
        if (next != signature && (desired != "idle" || now - settledAt >= 1))
        {
            signature = next; mode = desired; reason = purpose ?? "idle"; query = desired == "idle" ? "" : text ?? "";
            settledAt = lastChange = now; revision++;
            selected.Clear(); // release old salience, not old observations
        }
        var retained = new HashSet<string>(StringComparer.Ordinal);
        // A retained entity may have several reference IDs/views. Coalesce before
        // comparing state: traversing old/new records must not count as new input.
        var groups = (observations ?? Enumerable.Empty<JObject>()).Where(row => row != null &&
            !string.IsNullOrEmpty((string)row["entityId"]) && !string.IsNullOrEmpty((string)row["captureId"]) &&
            (double?)row["observedAt"] is double seen && !double.IsNaN(seen) && !double.IsInfinity(seen) && seen >= 0 && seen <= now)
            .GroupBy(row => (string)row["entityId"], StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(row => (double)row["observedAt"])
                .ThenBy(row => (string)row["captureId"], StringComparer.Ordinal)
                .ThenBy(row => (string)row["descriptionCaptureId"], StringComparer.Ordinal)
                .ThenBy(row => (string)row["description"], StringComparer.Ordinal).ToArray();
            var row = ordered[0];
            string id = group.Key, capture = (string)row["captureId"];
            double seen = (double)row["observedAt"];
            retained.Add(id);
            if (!entries.TryGetValue(id, out var entry)) entries[id] = entry = new Entry { id = id, observed = -1 };
            bool changed = false;
            if (entry.capture != capture && (seen > entry.observed || seen == entry.observed &&
                string.CompareOrdinal(capture, entry.capture) < 0))
            { changed = entry.capture != capture; entry.capture = capture; entry.observed = seen; }
            // A description is remembered evidence with its own capture. A newer
            // geometry observation must not re-date an inherited recognition.
            var described = ordered.FirstOrDefault(r => !string.IsNullOrWhiteSpace((string)r["description"]));
            if (described != null && (double)described["observedAt"] >= entry.descriptionObserved)
            {
                string description = (string)described["description"];
                string source = (string)described["descriptionCaptureId"] ?? (string)described["captureId"];
                changed |= entry.description != description || entry.descriptionCapture != source;
                // A newer geometry-only row cannot promote an older fallback
                // description over a recognition we have already retained.
                // Re-reading an inherited recognition does not renew its age.
                if (entry.descriptionCapture != source) entry.descriptionObserved = (double)described["observedAt"];
                entry.description = description; entry.descriptionCapture = source;
            }
            if (selected.Contains(id) && changed) { revision++; lastChange = now; }
        }
        foreach (string id in entries.Keys.Where(id => !retained.Contains(id)).ToArray()) entries.Remove(id);
        var pinned = new HashSet<string>((required ?? Enumerable.Empty<string>()).Where(id => entries.ContainsKey(id)), StringComparer.Ordinal);
        int slots = Slots(mode);
        var terms = Terms(query);
        var wanted = entries.Values.OrderByDescending(e => pinned.Contains(e.id) ? 10000 :
            Terms(e.description).Count(terms.Contains) * 10 + Math.Exp(-(now - e.observed) / 90) * 3 +
            (selected.Contains(e.id) && now - e.selectedAt < 2 ? 4 : 0))
            .ThenBy(e => e.id, StringComparer.Ordinal).Take(Math.Max(slots, pinned.Count)).Select(e => e.id).ToHashSet();
        wanted.UnionWith(pinned);
        if (!selected.SetEquals(wanted))
        {
            foreach (string id in wanted.Where(id => !selected.Contains(id))) entries[id].selectedAt = now;
            selected.Clear(); selected.UnionWith(wanted); lastChange = now; revision++;
        }
    }

    private static HashSet<string> Terms(string text)
    {
        text = (text ?? "").ToLowerInvariant(); var result = new HashSet<string>();
        for (int i = 1; i < text.Length; i++)
            if (char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i])) result.Add(text.Substring(i - 1, 2));
        return result;
    }
    private static int Slots(string value) => value == "investigate" ? 8 : value == "task" ? 4 : value == "dialogue" ? 3 : 6;
    public JObject Snapshot(string consumer, double now) => new JObject {
        ["source"] = "runtime-attention-state-v1", ["revision"] = revision, ["mode"] = mode,
        ["reason"] = reason, ["consumer"] = consumer, ["actionAuthorization"] = false,
        ["ordinarySlots"] = Slots(mode),
        ["optionalDetailUnits"] = mode == "investigate" ? 3600 : mode == "task" ? 2800 : mode == "dialogue" ? 1000 : 1200,
        ["updateIntervalSeconds"] = UpdateInterval, ["stableSeconds"] = Math.Max(0, now - lastChange),
        ["selectedEntities"] = new JArray(selected.OrderBy(id => id).Select(id => new JObject {
            ["entityId"] = id, ["sourceCaptureId"] = entries[id].capture,
            ["ageSeconds"] = Math.Max(0, now - entries[id].observed),
            ["descriptionCaptureId"] = entries[id].descriptionCapture
        })),
        ["scope"] = "Persistent attention over observed memory only. Selection is neither recognition, visibility nor action permission. " +
            "Consumers take a bounded snapshot; omitted memory remains recallable. Requirements, cancellation and actual outcomes stay in the task ledger."
    };
}
