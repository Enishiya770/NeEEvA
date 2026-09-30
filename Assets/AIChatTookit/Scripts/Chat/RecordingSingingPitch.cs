using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RecordingSingingDecisionCompiler
{
    public const string RecordingMusicContract =
        "\n音乐目标music只表达语义：不改现有演唱安排用{mode:preserve,reference:none,interval:none}；" +
        "参照某段已经播放的演唱中心用mode=match_recording、reference填music.samples的稳定clip、interval=none；" +
        "明确相对升降用mode=transpose、reference=none、interval填给定命名音程。参照某段统一音高、已有段连接并非表外操作。" +
        "music.samples是实际完整播放版本及中心观测，不是音乐调性；不从中心猜大小调、主音或key。目标确需未知调性而不能由这些事实确定时提出具体澄清。" +
        "模型不得计算/填写底层半音、音符频率、版本或XML；程序固定来源与播放版本，执行前重新核验。";
    public const string RecordingMusicReviewContract =
        "\n若goal.music非空，另从用户完整原话独立核对音乐目标，expected.music填{mode,reference,interval}，" +
        "reference为真实稳定clip而非清单序号；与goal.music.spec一致才能approved。没有音乐修改时可省略music或用preserve/none/none。" +
        "music.samples的centerMidi只表示已播放版本的演唱中心，不证明音乐调性。核对参照对象与明确音程，不替角色发明音程，也不因目标中心近似而宣称同调。";

    private static readonly string[] RecordingMusicIntervals = {
        "+half_step", "-half_step", "+whole_step", "-whole_step", "+m3", "-m3", "+M3", "-M3",
        "+P4", "-P4", "+P5", "-P5", "+P8", "-P8" };

    public static JObject RecordingMusicSchema(IEnumerable<string> references = null, bool perform = true)
    {
        JObject Choice(IEnumerable<string> values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        JObject Branch(string mode, JObject reference, JObject interval) => new JObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject {
                ["mode"] = Choice(new[] { mode }), ["reference"] = reference, ["interval"] = interval },
            ["required"] = new JArray("mode", "reference", "interval") };
        var branches = new JArray(Branch("preserve", Choice(new[] { "none" }), Choice(new[] { "none" })));
        if (perform)
        {
            string[] available = references?.Where(r => !string.IsNullOrWhiteSpace(r) && r != "none").Distinct().ToArray();
            if (available == null || available.Length > 0)
                branches.Add(Branch("match_recording", available == null
                    ? new JObject { ["type"] = "string", ["minLength"] = 6, ["maxLength"] = 160,
                        ["pattern"] = "^clip:[A-Za-z0-9_.:-]+$" } : Choice(available), Choice(new[] { "none" })));
            branches.Add(Branch("transpose", Choice(new[] { "none" }), Choice(RecordingMusicIntervals)));
        }
        return new JObject { ["type"] = "object", ["anyOf"] = branches };
    }

    private static JArray RecordingMusicSamples(RecordingSingingDecisionContext context)
    {
        try { return JObject.Parse(context.Facts ?? "{}")["music"]?["samples"] as JArray ?? new JArray(); }
        catch (JsonException) { return new JArray(); }
    }

    private static void AddRecordingMusicSchema(JObject properties, RecordingSingingDecisionContext context, string decision)
    {
        if (!context.RequireGroundedResolution) return;
        properties["music"] = RecordingMusicSchema(RecordingMusicSamples(context).OfType<JObject>()
            .Select(s => (string)s["clip"]).Where(k => context.Materials.Any(m => m.Key == k)), decision == "perform");
    }

    public static bool TryBindRecordingMusic(JObject spec, RecordingSingingDecisionContext context,
        string[] refs, out JObject binding, out string error)
    {
        binding = null; error = "invalid-recording-music";
        if (spec == null) { error = ""; return true; } // Existing saved/legacy proposals preserve their rendering.
        if (spec.Count != 3 || new[] { "mode", "reference", "interval" }.Any(k => spec[k]?.Type != JTokenType.String)) return false;
        string mode = (string)spec["mode"], reference = (string)spec["reference"], interval = (string)spec["interval"];
        if (mode == "preserve")
        { if (reference != "none" || interval != "none") return false; error = ""; return true; }
        if (refs == null || refs.Length == 0 || refs.Length > 8) return false;
        if (mode != "match_recording" && mode != "transpose") return false;
        if (mode == "match_recording" ? reference == "none" || interval != "none" :
            reference != "none" || !RecordingMusicIntervals.Contains(interval)) return false;
        var samples = RecordingMusicSamples(context).OfType<JObject>().ToArray();
        JObject Sample(string clip)
        {
            var rows = samples.Where(s => (string)s["clip"] == clip).ToArray();
            var source = context.Materials.FirstOrDefault(m => m.ClipRef == clip);
            if (rows.Length != 1 || source == null || source.Revision != (string)rows[0]["clipRevision"] ||
                (bool?)rows[0]["fullyPlayed"] != true || ((int?)rows[0]["pitchRevision"] ?? 0) <= 0 ||
                !double.TryParse(rows[0]["centerMidi"]?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double center) ||
                double.IsNaN(center) || double.IsInfinity(center) || center <= 0 || center > 127) return null;
            return (JObject)rows[0].DeepClone();
        }
        var versions = new JArray();
        foreach (string clip in refs)
        {
            var material = context.Materials.FirstOrDefault(m => m.ClipRef == clip);
            if (material == null || material.Revision == "pending") { error = "recording-music-material-not-prepared"; return false; }
            versions.Add(new JObject { ["clip"] = clip, ["revision"] = material.Revision });
        }
        var bases = new JArray();
        if (mode == "match_recording")
        {
            var sample = Sample(reference);
            if (sample == null) { error = "recording-music-reference-unavailable"; return false; }
            bases.Add(sample);
        }
        else foreach (string clip in refs)
        {
            var sample = Sample(clip);
            if (sample == null) { error = "recording-music-playback-baseline-unavailable"; return false; }
            bases.Add(sample);
        }
        binding = new JObject { ["spec"] = spec.DeepClone(), ["sourceVersions"] = versions, ["playbackBases"] = bases,
            ["scope"] = "Frozen performed-center target, not a musical key/tonality determination. No action or source approval." };
        error = ""; return true;
    }

    private static bool TryCompileRecordingMusic(JObject value, RecordingSingingDecisionContext context,
        string[] refs, out string attributes, out string error)
    {
        attributes = ""; error = "invalid-recording-music";
        var spec = value.Property("music") == null
            ? new JObject { ["mode"] = "preserve", ["reference"] = "none", ["interval"] = "none" } : value["music"] as JObject;
        if (spec == null) return false;
        if ((string)value["decision"] != "perform" && (string)spec["mode"] != "preserve") return false;
        if (!TryBindRecordingMusic(spec, context, refs, out var binding, out error)) return false;
        bool continuation = context.Recovery != null &&
            (value.Property("goal_transition") == null || (string)value["goal_transition"] == "continue");
        if ((string)value["decision"] == "perform" && continuation && context.Recovery.Music == null && binding != null)
        { error = "recording-music-goal-change-needs-transition"; return false; }
        if ((string)value["decision"] == "perform" && continuation && context.Recovery.Music != null)
        {
            if (binding != null && !JToken.DeepEquals(binding, context.Recovery.Music))
            { error = "recording-music-goal-change-needs-transition"; return false; }
            if (!TryBindRecordingMusic(context.Recovery.Music["spec"] as JObject, context, refs, out var current, out error) ||
                !JToken.DeepEquals(current, context.Recovery.Music))
            { error = "recording-music-playback-version-changed"; return false; }
            binding = context.Recovery.Music;
        }
        if (binding != null) attributes = " recording_music=\"" + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(binding.ToString(Formatting.None))) + "\"";
        return true;
    }
}
