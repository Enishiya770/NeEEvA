using System;

// One policy for queue boundaries and server sentence splitting. Units approximate
// speaking length, not tokens; language declarations and every word stay intact.
public static class SpeechChunkPolicy
{
    public const float MaximumUnits = 32;
    private const float PreferredUnits = 24;
    public static float Units(string text)
    {
        float count = 0;
        foreach (char c in text ?? "") if (!char.IsWhiteSpace(c)) count += c < 128 ? .35f : 1f;
        return count;
    }
    public static bool Bounded(string text) => Units(text) <= MaximumUnits;
    public static int Boundary(string text, bool first, int firstLimit)
    {
        float units = 0;
        int weak = -1;
        float limit = first ? Math.Min(MaximumUnits, Math.Max(8, firstLimit)) : PreferredUnits;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsWhiteSpace(c)) units += c < 128 ? .35f : 1f;
            bool strong = "。！？!?\n".IndexOf(c) >= 0 || c == '.' &&
                (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));
            // A short greeting/acknowledgement can share the following clause.
            // EOF still flushes a genuinely short complete answer immediately.
            if (strong && units >= 8) return i;
            if (strong && units < 8) limit = MaximumUnits;
            if (units >= 8 && (strong || "，,、；;：:".IndexOf(c) >= 0 || char.IsWhiteSpace(c))) weak = i;
            if (units >= limit)
            {
                if (weak >= 0) return weak;
                // Do not cut an English word or a Unicode surrogate pair.
                if (c < 128 && char.IsLetterOrDigit(c)) continue;
                return char.IsHighSurrogate(c) && i + 1 < text.Length ? i + 1 : i;
            }
        }
        return -1;
    }
}
