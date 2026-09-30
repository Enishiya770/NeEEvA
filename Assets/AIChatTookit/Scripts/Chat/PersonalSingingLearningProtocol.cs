using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

public sealed class PersonalLearningCaptureLease
{
    public string Profile, Session, Group, SessionRoot;
    public int Capture;
}

// Profile/group are frozen when the recorder begins, not when an ASR reply arrives.
public sealed class PersonalLearningCaptureLeases
{
    private readonly Dictionary<string, PersonalLearningCaptureLease> leases = new Dictionary<string, PersonalLearningCaptureLease>();
    private bool enabled;
    public bool Enabled => enabled;
    public bool Contains(string root, int capture) => enabled && leases.ContainsKey(root + "/" + capture);
    public void Abort(string root, int capture) => leases.Remove(root + "/" + capture);
    public void SetEnabled(bool value) { enabled = value; if (!value) leases.Clear(); }
    public void Begin(string root, int capture, string profile, string session, string group)
    {
        if (!enabled || string.IsNullOrWhiteSpace(root) || capture <= 0 ||
            string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(group)) return;
        if (leases.Count >= 64) return;
        string key = root + "/" + capture;
        if (!leases.ContainsKey(key)) leases.Add(key, new PersonalLearningCaptureLease {
            Profile = profile, Session = session, Group = group ?? "", SessionRoot = root, Capture = capture });
    }
    public bool TrySeal(string root, int capture, out PersonalLearningCaptureLease value)
    {
        string key = root + "/" + capture;
        if (!enabled || !leases.TryGetValue(key, out value)) { value = null; return false; }
        leases.Remove(key); return true;
    }
}

public static class PersonalSingingLearningProtocol
{
    public static readonly string[] Labels = { "speech", "singing", "environment", "uncertain" };
    public static readonly string[] Sources = { "user", "playback", "mixed", "unknown" };
    public static string Sha256(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    public static bool TryWave(byte[] wav, out int rate, out int channels, out int frames, out int dataOffset, out string error)
    {
        rate = channels = frames = dataOffset = 0; error = "只支持完整 PCM16 WAV";
        if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return false;
        int dataLength = 0, align = 0, bits = 0, format = 0;
        for (long at = 12; at + 8 <= wav.Length;)
        {
            int offset = (int)at; uint length = BitConverter.ToUInt32(wav, offset + 4);
            long next = at + 8L + length;
            if (next > wav.Length) return false;
            string kind = Encoding.ASCII.GetString(wav, offset, 4);
            if (kind == "fmt " && length >= 16)
            {
                format = BitConverter.ToUInt16(wav, offset + 8); channels = BitConverter.ToUInt16(wav, offset + 10);
                rate = BitConverter.ToInt32(wav, offset + 12); align = BitConverter.ToUInt16(wav, offset + 20);
                bits = BitConverter.ToUInt16(wav, offset + 22);
            }
            if (kind == "data") { dataOffset = offset + 8; dataLength = (int)length; }
            at = next + (length % 2);
        }
        if (format != 1 || bits != 16 || channels < 1 || channels > 8 || rate <= 0 || align != channels * 2 || dataLength <= 0 || dataLength % align != 0) return false;
        frames = dataLength / align; error = null; return true;
    }

    public static bool TryRange(string startText, string endText, bool seconds, int rate, int count,
        out int start, out int end, out string error)
    {
        start = end = 0; error = "请输入原音频内有效起止范围；空白不会使用旧选区";
        var style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (!decimal.TryParse(startText, style, CultureInfo.InvariantCulture, out var a) ||
            !decimal.TryParse(endText, style, CultureInfo.InvariantCulture, out var b) || rate <= 0 || count <= 0) return false;
        if (!seconds && (a != decimal.Truncate(a) || b != decimal.Truncate(b))) return false;
        if (seconds)
        {
            if (a < 0 || b > (decimal)count / rate) return false;
            a = decimal.Round(a * rate, 0, MidpointRounding.AwayFromZero); b = decimal.Round(b * rate, 0, MidpointRounding.AwayFromZero);
        }
        if (a < 0 || b <= a || b > count) return false;
        start = (int)a; end = (int)b; error = null; return true;
    }

    public static bool ValidateRanges(JArray ranges, int sampleCount, bool requireComplete, out string error)
    {
        error = null; int previous = 0;
        if (ranges == null || sampleCount <= 0) { error = "原始录音信息不可用"; return false; }
        var sorted = new List<JObject>(); foreach (var item in ranges) { if (!(item is JObject value)) { error = "区间无效"; return false; } sorted.Add(value); }
        sorted.Sort((a, b) => ((int?)a["start_sample"] ?? -1).CompareTo((int?)b["start_sample"] ?? -1));
        foreach (var row in sorted)
        {
            int a = (int?)row["start_sample"] ?? -1, b = (int?)row["end_sample"] ?? -1;
            if (Array.IndexOf(Labels, (string)row["label"]) < 0 || a < previous || b <= a || b > sampleCount ||
                requireComplete && a != previous) { error = "区间重叠、越界或未完整覆盖"; return false; }
            previous = b;
        }
        if (requireComplete && (sorted.Count == 0 || previous != sampleCount)) { error = "完整复核要求覆盖整段；未知部分请明确标 uncertain"; return false; }
        return true;
    }
}
