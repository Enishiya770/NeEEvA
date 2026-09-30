using System;
using System.Collections;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyRoomStreamTelemetry
    {
        public ArdyServiceErrorObservation serviceError;
        public string eventName, requestId, sessionId, stage, lastFailureCategory, lastFailureReason;
        public string historyCode, historyReason;
        public int latestRejectedPrefixOrdinal;
        public int operationRevision, stepIndex, controlRevision, recoveryCount, consecutiveRecoveries, bufferedFrames;
        public float bufferSeconds, readyTargetSeconds, requestElapsedSeconds, roundTripSeconds, backendSeconds,
            backendQueueSeconds, featureSeconds, planningSeconds, parseSeconds, auditSeconds, frameSeconds, largestRecentFrameSeconds,
            controlLatencySeconds;
        public bool pending, prebuffering, recovering;
    }

    public sealed partial class ArdyRoomLocomotionController
    {
        [Serializable] private sealed class ServiceStreamDiagnostic
        {
            public string stage;
            public float elapsedMs;
            public int deadlineMs;
        }
        [Serializable] private sealed class ServiceErrorDetails { public ServiceStreamDiagnostic streamDiagnostic; public ServiceQualityTimings timings; }
        [Serializable] private sealed class ServiceQualityTimings { public int qualityAttempts; public ServiceQualityCandidate[] qualityCandidates; }
        [Serializable] private sealed class ServiceQualityCandidate { public int attempt; public float windowMs; public bool accepted; public ArdyServiceQualityReason[] reasons; }
        [Serializable] private sealed class ServiceErrorBody { public string code, message; public ServiceErrorDetails details; }
        [Serializable] private sealed class ServiceErrorEnvelope { public ServiceErrorBody error; }
        private static string DescribeServiceFailure(string response)
        {
            if (string.IsNullOrEmpty(response) || response.Length > 32768) return "";
            try
            {
                var diagnostic = JsonUtility.FromJson<ServiceErrorEnvelope>(response)?.error?.details?.streamDiagnostic;
                if (diagnostic == null || float.IsNaN(diagnostic.elapsedMs) || float.IsInfinity(diagnostic.elapsedMs) ||
                    diagnostic.elapsedMs < 0 || diagnostic.elapsedMs > 300000 || diagnostic.deadlineMs < 1 || diagnostic.deadlineMs > 20000) return "";
                string stage;
                switch (diagnostic.stage)
                {
                    case "queued": stage = "本地请求排队"; break;
                    case "feature-wait": stage = "等待动作特征（含通信和远端排队）"; break;
                    case "backend-gate": stage = "等待动作网络执行"; break;
                    case "generation": stage = "动作网络生成"; break;
                    default: return "";
                }
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "；服务阶段：{0}，已用 {1:F2} 秒，预算 {2:F1} 秒", stage, diagnostic.elapsedMs / 1000f, diagnostic.deadlineMs / 1000f);
            }
            catch (ArgumentException) { return ""; }
        }
        private sealed class ServiceRequestException : InvalidOperationException
        {
            public readonly string Code;
            public ServiceRequestException(string message, string response) : base(message + DescribeServiceFailure(response) + ServiceFailureCodeSuffix(response))
            {
                // Error bodies can be empty or non-JSON (proxy/transport failures).
                // Only the machine-readable code is used for diagnosis, never execution.
                try { if (!string.IsNullOrEmpty(response) && response.Length <= 32768)
                    Code = SafeServiceCode(JsonUtility.FromJson<ServiceErrorEnvelope>(response)?.error?.code); }
                catch (ArgumentException) { }
            }
        }
        [Serializable] private sealed class StreamTimings
        {
            public float totalMs, backendMs, backendGateMs, queueMs, featureMs;
            public int qualityAttempts;
        }
        private string streamEventName, streamLastFailureCategory, streamLastFailureReason;
        private float streamRoundTrip, streamBackend, streamBackendQueue, streamFeature, streamPlanning, streamParse, streamAudit;
        private float streamRecentFramePeak, streamRecentStepCost;
        private bool streamRequestPending;

        public ArdyRoomStreamTelemetry StreamTelemetry => new ArdyRoomStreamTelemetry {
            serviceError = LastErrorResponseJson == null ? null : lastServiceError,
            eventName = streamEventName, requestId = requestId, sessionId = streamSessionId, stage = Stage,
            lastFailureCategory = streamLastFailureCategory, lastFailureReason = streamLastFailureReason,
            historyCode = warmHistoryObservation?.code, historyReason = warmHistoryObservation?.reason,
            latestRejectedPrefixOrdinal = LatestRejectedPrefixOrdinal,
            operationRevision = OperationRevision, stepIndex = streamStepIndex, controlRevision = StreamControlRevision,
            recoveryCount = StreamRecoveryCount, consecutiveRecoveries = streamConsecutiveRecoveries,
            bufferedFrames = Mathf.Max(0, Mathf.CeilToInt(StreamBufferSeconds * 20f)),
            bufferSeconds = StreamBufferSeconds, readyTargetSeconds = StreamReadyTargetSeconds,
            requestElapsedSeconds = streamRequestPending ? Mathf.Max(0, Time.realtimeSinceStartup - streamDispatchTime) : 0,
            roundTripSeconds = streamRoundTrip, backendSeconds = streamBackend, backendQueueSeconds = streamBackendQueue,
            featureSeconds = streamFeature, planningSeconds = streamPlanning, parseSeconds = streamParse, auditSeconds = streamAudit,
            frameSeconds = Time.unscaledDeltaTime, largestRecentFrameSeconds = streamRecentFramePeak,
            controlLatencySeconds = StreamControlLatencySeconds, pending = streamRequestPending,
            prebuffering = streamPreparedClip != null || streamSessionId != null && ownedClip == null,
            recovering = streamRecovering
        };

        // The native backend can evaluate up to three quality candidates for one prefix.
        // A run of quick single-candidate replies must not shrink their delivery budget
        // to .38s (the former .45s lead minus the protected final sample).
        private float StreamLeadSeconds => Mathf.Clamp(streamRecentStepCost + .15f + streamRecentFramePeak * 2f, .65f, .90f);
        private float StreamReadyTargetSeconds => Mathf.Clamp(Mathf.Ceil((StreamLeadSeconds + .1f) / .4f) * .4f, .8f, 1.2f);

        private void RecordStreamEvent(string name, string reason = null, string category = null)
        {
            streamEventName = name;
            if (reason != null) { streamLastFailureReason = reason; streamLastFailureCategory = category; }
            Debug.Log("[ARDY room/stream] " + JsonUtility.ToJson(StreamTelemetry), this);
        }

        private static string StreamFailureCategory(Exception error)
        {
            if (error is PoseHistoryException) return "pose-history";
            var execution = error as ExecutionException;
            // Structured service codes, never the server's free-form prose, classify a transport fault.
            if (error is ServiceRequestException service)
                return service.Code == "stream_quality_rejected" ? "motion-quality" : "service-request";
            if (execution?.Reason == ArdyRoomExecutionReason.OwnershipLost) return "ownership";
            if (error.Message.Contains("净距")) return "user-clearance";
            if (error.Message.Contains("步态") || error.Message.Contains("关节") || error.Message.Contains("stream_quality_rejected")) return "motion-quality";
            if (execution?.Reason == ArdyRoomExecutionReason.NavigationRejected || execution?.Reason == ArdyRoomExecutionReason.GeneratedPathRejected)
                return "navigation";
            return "service-request";
        }

        private IEnumerator GuardedStreamRun(IEnumerator routine, int token)
        {
            try
            {
                while (Current(token))
                {
                    object current = null; Exception failure = null; bool more = false;
                    try { more = routine.MoveNext(); if (more) current = routine.Current; }
                    catch (Exception error) { failure = error; }
                    if (failure != null)
                    {
                        RecordStreamEvent("failed", failure.Message, StreamFailureCategory(failure));
                        Fail(failure.Message, (failure as ExecutionException)?.Reason ?? ArdyRoomExecutionReason.GenerationFailed);
                        yield break;
                    }
                    if (!more) yield break;
                    yield return current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }
    }
}
