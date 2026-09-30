using System;
using System.Collections.Generic;

/// <summary>Capture provenance, not a text/speaker classifier. A sealed self-speech
/// prefix remains rejected when a continuation receives a new capture serial.</summary>
public sealed class RecordingContinuationIsolation
{
    private struct Link { public int Parent; public float Seconds; }
    private readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
    private readonly Dictionary<int, float> rejected = new Dictionary<int, float>();

    public void Bind(int capture, int earlier, float prefixSeconds)
    {
        if (capture <= earlier || earlier <= 0 || !Positive(prefixSeconds)) return;
        links[capture] = new Link { Parent = earlier, Seconds = prefixSeconds };
    }

    public bool Reject(int capture, bool completed, string reason, float seconds)
    {
        if (capture <= 0 || !completed || reason != "self-speech" || !Positive(seconds)) return false;
        rejected.TryGetValue(capture, out float previous);
        rejected[capture] = Math.Max(previous, seconds);
        return seconds > previous;
    }

    public bool IsRejected(int capture) => rejected.ContainsKey(capture);

    // Only inherited PCM is removed from an otherwise new user's capture.
    // Descendants retain the exact duration rather than becoming wholly rejected.
    public float InheritedRejectedSeconds(int capture)
    {
        float bound = float.MaxValue;
        float result = 0f;
        while (links.TryGetValue(capture, out Link link))
        {
            bound = Math.Min(bound, link.Seconds);
            capture = link.Parent;
            if (rejected.TryGetValue(capture, out float seconds))
                result = Math.Max(result, Math.Min(bound, seconds));
        }
        return result;
    }

    private static bool Positive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);

    public static int ClampPreRoll(int start, int current, int rejectedEnd, int ringFrames)
    {
        if (ringFrames <= 0 || start < 0 || current < 0 || rejectedEnd < 0 ||
            start >= ringFrames || current >= ringFrames || rejectedEnd >= ringFrames) return start;
        int available = (current - start + ringFrames) % ringFrames;
        int afterFence = (current - rejectedEnd + ringFrames) % ringFrames;
        return afterFence < available ? rejectedEnd : start;
    }

    // Strict PCM crop: unlike a singing-material crop, a short remaining suffix
    // must never cause the rejected prefix to be returned unchanged.
    public static bool TryRemovePrefix(byte[] wav, float seconds, out byte[] suffix, out float removedSeconds)
    {
        suffix = null; removedSeconds = 0f;
        if (!Positive(seconds) || wav == null || wav.Length < 44 ||
            BitConverter.ToUInt32(wav, 0) != 0x46464952 || BitConverter.ToUInt32(wav, 8) != 0x45564157) return false;
        int rate = 0, align = 0, format = 0;
        for (long pos = 12; pos + 8 <= wav.Length; )
        {
            int i = (int)pos; uint size = BitConverter.ToUInt32(wav, i + 4);
            if (pos + 8 + size > wav.Length) return false;
            uint name = BitConverter.ToUInt32(wav, i);
            if (name == 0x20746d66 && size >= 16)
            {
                format = BitConverter.ToUInt16(wav, i + 8);
                rate = BitConverter.ToInt32(wav, i + 12);
                align = BitConverter.ToUInt16(wav, i + 20);
            }
            if (name == 0x61746164)
            {
                if (format != 1 || rate <= 0 || align <= 0 || size % align != 0) return false;
                long frames = size / align;
                // Nearest frame avoids cutting a real first sample due to float rounding.
                long skipFrames = Math.Min(frames, Math.Max(0L, (long)Math.Round((double)seconds * rate)));
                int skip = (int)(skipFrames * align), remaining = (int)size - skip;
                removedSeconds = skipFrames / (float)rate;
                if (remaining <= 0) { suffix = new byte[0]; return true; }
                suffix = new byte[i + 8 + remaining];
                Buffer.BlockCopy(wav, 0, suffix, 0, i + 8);
                Buffer.BlockCopy(wav, i + 8 + skip, suffix, i + 8, remaining);
                Buffer.BlockCopy(BitConverter.GetBytes(suffix.Length - 8), 0, suffix, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(remaining), 0, suffix, i + 4, 4);
                return true;
            }
            pos += 8L + size + (size & 1);
        }
        return false;
    }
}
