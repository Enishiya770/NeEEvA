using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable] public sealed class ArdyServiceQualityReason
    {
        public string code, joint;
        public int frame;
        public float value, minimum, maximum;
    }
    [Serializable] public sealed class ArdyServiceQualityAttempt
    {
        public int attempt;
        public bool accepted;
        public float windowMs;
        public ArdyServiceQualityReason[] reasons;
    }
    [Serializable] public sealed class ArdyServiceErrorObservation
    {
        public string requestId, observedUtc, code, message;
        public long httpStatus;
        public int operationRevision, qualityAttempts;
        public bool detailOmitted;
        public ArdyServiceQualityAttempt[] candidates;
        public string scope = "Bounded service rejection diagnostic for this exact request. No motion was accepted from this response; it grants no retry or reduced safety/quality thresholds.";
    }

    public sealed partial class ArdyRoomLocomotionController
    {
        private ArdyServiceErrorObservation lastServiceError;

        private static string SafeServiceText(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = Regex.Replace(text, @"(?i)(?:authorization\s*[:=]\s*(?:bearer\s+)?|bearer\s+|(?:api[_-]?key|token|password)\s*[:=]\s*)\S+", "[redacted]");
            text = Regex.Replace(text, @"\bsk-[A-Za-z0-9_-]+", "[redacted]");
            text = Regex.Replace(text, @"[\x00-\x1f\x7f]", " ");
            return text.Length <= limit ? text : text.Substring(0, limit);
        }
        private static string SafeServiceCode(string code) => !string.IsNullOrEmpty(code) && code.Length <= 64 &&
            Regex.IsMatch(code, "^[a-zA-Z0-9_-]+$") ? code : "";
        private static bool ServiceFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string ServiceFailureCodeSuffix(string response)
        {
            if (string.IsNullOrEmpty(response) || response.Length > 32768) return "";
            try
            {
                string code = SafeServiceCode(JsonUtility.FromJson<ServiceErrorEnvelope>(response)?.error?.code);
                return code.Length == 0 ? "" : " [service-code=" + code + "]";
            }
            catch (ArgumentException) { return ""; }
        }

        internal static ArdyServiceErrorObservation ReadServiceErrorObservation(string response, long httpStatus,
            string request, int operation)
        {
            var result = new ArdyServiceErrorObservation { requestId = SafeServiceText(request, 96),
                operationRevision = operation, httpStatus = httpStatus, observedUtc = DateTime.UtcNow.ToString("O"),
                code = "", message = "", candidates = Array.Empty<ArdyServiceQualityAttempt>() };
            if (string.IsNullOrEmpty(response) || response.Length > 32768) { result.detailOmitted = true; return result; }
            try
            {
                var error = JsonUtility.FromJson<ServiceErrorEnvelope>(response)?.error;
                if (error == null) { result.detailOmitted = true; return result; }
                result.code = SafeServiceCode(error.code); result.message = SafeServiceText(error.message, 240);
                var timings = error.details?.timings;
                result.qualityAttempts = Mathf.Clamp(timings?.qualityAttempts ?? 0, 0, 3);
                var candidates = new List<ArdyServiceQualityAttempt>();
                foreach (var candidate in timings?.qualityCandidates ?? Array.Empty<ServiceQualityCandidate>())
                {
                    if (candidates.Count >= 3) { result.detailOmitted = true; break; }
                    if (candidate == null || candidate.attempt < 1 || candidate.attempt > 3 ||
                        !ServiceFinite(candidate.windowMs) || candidate.windowMs < 0 || candidate.windowMs > 300000)
                    { result.detailOmitted = true; continue; }
                    var reasons = new List<ArdyServiceQualityReason>();
                    foreach (var item in candidate.reasons ?? Array.Empty<ArdyServiceQualityReason>())
                    {
                        if (reasons.Count >= 6) { result.detailOmitted = true; break; }
                        if (item == null || (item.code != "standing_height" && item.code != "root_step" && item.code != "joint_step") ||
                            item.frame < 0 || item.frame > 56 || !ServiceFinite(item.value) ||
                            !ServiceFinite(item.minimum) || !ServiceFinite(item.maximum))
                        { result.detailOmitted = true; continue; }
                        reasons.Add(new ArdyServiceQualityReason { code = item.code, joint = SafeServiceCode(item.joint),
                            frame = item.frame, value = item.value, minimum = item.minimum, maximum = item.maximum });
                    }
                    candidates.Add(new ArdyServiceQualityAttempt { attempt = candidate.attempt, accepted = candidate.accepted,
                        windowMs = candidate.windowMs, reasons = reasons.ToArray() });
                }
                result.candidates = candidates.ToArray();
            }
            catch (ArgumentException) { result.detailOmitted = true; }
            return result;
        }
    }
}
