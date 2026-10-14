using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NeEEvA.Motion
{
    /// <summary>Service evidence only. This never grants a locomotion lease.</summary>
    public static class ArdyStreamQualityFailure
    {
        public static JObject Read(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 32768) return null;
            try
            {
                var error = JObject.Parse(body)["error"] as JObject;
                return (string)error?["code"] == "stream_quality_rejected" ? (JObject)error.DeepClone() : null;
            }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        public static bool SupportsFreshHistoryRecovery(JObject error)
        {
            try { return ValidateRecoveryEvidence(error); }
            catch (Exception e) when (e is ArgumentException || e is InvalidCastException || e is FormatException || e is OverflowException)
            { return false; }
        }

        private static bool ValidateRecoveryEvidence(JObject error)
        {
            if ((string)error?["code"] != "stream_quality_rejected" ||
                !(error.SelectToken("details.timings.qualityCandidates") is JArray candidates) || candidates.Count != 3 ||
                (int?)error.SelectToken("details.timings.qualityAttempts") != 3) return false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!(candidates[i] is JObject c) || (int?)c["attempt"] != i + 1 || (bool?)c["accepted"] != false ||
                    !(c["reasons"] is JArray reasons) || reasons.Count < 1 || reasons.Count > 3) return false;
                foreach (var reason in reasons.OfType<JObject>())
                {
                    string code = (string)reason["code"];
                    // Corrupt/nonfinite output and unknown contracts must terminate.
                    if (code != "standing_height" && code != "root_step" && code != "joint_step") return false;
                    if (!FiniteNumber(reason["value"]) || !FiniteNumber(reason["maximum"]) ||
                        (double)reason["maximum"] <= 0 || !(reason["frame"]?.Type == JTokenType.Integer) ||
                        (int)reason["frame"] < 0 || (int)reason["frame"] >= 8) return false;
                    if (code == "standing_height" && (!FiniteNumber(reason["minimum"]) || (double)reason["minimum"] <= 0)) return false;
                    if (code == "joint_step" && (reason["joint"]?.Type != JTokenType.String ||
                        !ArdyLiveProtocol.JointNames.Contains((string)reason["joint"]))) return false;
                }
                if (reasons.OfType<JObject>().Count() != reasons.Count) return false;
            }
            return true;
        }

        private static bool FiniteNumber(JToken token) => token != null &&
            (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
            !double.IsNaN((double)token) && !double.IsInfinity((double)token);
    }
}
