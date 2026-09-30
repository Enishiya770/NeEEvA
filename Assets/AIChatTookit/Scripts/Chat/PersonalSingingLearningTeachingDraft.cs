using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// Pure source-sample edits, shared by pointer selection and verbal corrections.
public static class PersonalSingingLearningTeachingDraft
{
    public static bool TryReplace(JArray original, int count, int start, int end, string label, out JArray result)
    {
        result = null;
        if (start < 0 || end <= start || end > count || Array.IndexOf(PersonalSingingLearningProtocol.Labels, label) < 0 || !PersonalSingingLearningProtocol.ValidateRanges(original, count, false, out _)) return false;
        var next = new JArray();
        foreach (JObject old in original)
        {
            int a = (int)old["start_sample"], b = (int)old["end_sample"];
            if (b <= start || a >= end) next.Add(old.DeepClone());
            else
            {
                if (a < start) { var left = (JObject)old.DeepClone(); left["end_sample"] = start; left["tolerance_samples"] = 0; next.Add(left); }
                if (b > end) { var right = (JObject)old.DeepClone(); right["start_sample"] = end; right["tolerance_samples"] = 0; next.Add(right); }
            }
        }
        next.Add(Region(start, end, label));
        result = Merge(next); return PersonalSingingLearningProtocol.ValidateRanges(result, count, false, out _);
    }
    public static bool TryResize(JArray original, int count, int oldStart, int oldEnd, int newStart, int newEnd, out JArray result)
    {
        result = null;
        var region = original?.FirstOrDefault(r => (int?)r["start_sample"] == oldStart && (int?)r["end_sample"] == oldEnd);
        if (region == null || newStart < 0 || newEnd <= newStart || newEnd > count) return false;
        // Removing part of a class is not evidence that it became another class.
        if (!TryReplace(original, count, oldStart, oldEnd, "uncertain", out var cleared)) return false;
        return TryReplace(cleared, count, newStart, newEnd, (string)region["label"], out result);
    }
    private static JObject Region(int a, int b, string label) => new JObject { ["start_sample"] = a, ["end_sample"] = b, ["label"] = label, ["tolerance_samples"] = 0 };
    private static JArray Merge(JArray regions)
    {
        var result = new JArray();
        foreach (JObject row in regions.OrderBy(r => (int)r["start_sample"]))
        {
            var previous = result.Last as JObject;
            if (previous != null && (int)previous["end_sample"] == (int)row["start_sample"] && (string)previous["label"] == (string)row["label"] && ((int?)previous["tolerance_samples"] ?? 0) == 0 && ((int?)row["tolerance_samples"] ?? 0) == 0) previous["end_sample"] = row["end_sample"].DeepClone();
            else result.Add(row.DeepClone());
        }
        return result;
    }
}
