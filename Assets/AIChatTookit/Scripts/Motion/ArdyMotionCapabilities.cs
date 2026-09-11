using System;
using System.Text.RegularExpressions;

namespace NeEEvA.Motion
{
    /// <summary>Known unsupported requests, not a general natural-language capability classifier.</summary>
    public static class ArdyMotionCapabilities
    {
        private static readonly Regex IndependentFingers = new Regex(
            @"\b(index|middle|ring|little|pinky)\s+(finger|fingers)\b|\bthumb(s|s[- ]up)?\b|\b(peace|victory)[- ]sign\b|\bv[- ](sign|shape)\b|\b(clench|clenches|clenching|make|makes|making)\b.{0,24}\bfist(s)?\b|\b(wiggle|wiggles|wiggling|snap|snaps|snapping|cross|crosses|crossing|count|counts|counting)\b.{0,30}\bfinger(s)?\b|食指|中指|无名指|小指|拇指|打响指|握拳|人差し指|中指|親指",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string UnsupportedReason(string description)
        {
            if (!string.IsNullOrEmpty(description) && IndependentFingers.IsMatch(description))
                return "independent-fingers-unsupported: this route controls whole hands/wrists, not individual index/middle/thumb joints or finger shapes. Preserve the user's intent; explain the limit instead of claiming the finger gesture occurred.";
            return null;
        }

        public static void ValidateDescription(string description)
        {
            string reason = UnsupportedReason(description);
            if (reason != null) throw new ArgumentException(reason);
        }
    }
}
