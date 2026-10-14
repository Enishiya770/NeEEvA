using System;
using System.Collections;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyRoomStreamTelemetry
    {
        public string eventName, requestId, sessionId, stage, lastFailureCategory, lastFailureReason;
        public string historyCode, historyReason;
        public int latestRejectedPrefixOrdinal;
        public int operationRevision, stepIndex, controlRevision, recoveryCount, consecutiveRecoveries, bufferedFrames;
        public int qualityRecoveryCount, qualityRecoveryLimit;
        public string qualityFailureEvidencePath;
        public int controlRecoveryCount, controlRecoveryLimit;
        public string controlFailureEvidencePath, controlPlanningMode, warmContinuationReason;
        public float controlHardInitialSpeed, controlNominalInitialSpeed, controlTrackingError, controlRemainingDistance;
        public string failureRequestId, failureSessionId, failureObservedUtc, failureStage;
        public ArdyRoomStreamHandoff lastHandoff;
        public int failureOperationRevision;
        public bool failureAppliesToCurrentOperation;
        public float bufferSeconds, readyTargetSeconds, requestElapsedSeconds, roundTripSeconds, backendSeconds,
            backendQueueSeconds, featureSeconds, planningSeconds, parseSeconds, auditSeconds, frameSeconds, largestRecentFrameSeconds,
            controlLatencySeconds;
        public bool pending, prebuffering, recovering;
    }

    [Serializable]
    public sealed class ArdyRoomStreamHandoff
    {
        public string reason, sessionId, requestId, stage, observedUtc;
        public int operationRevision, wireRevision, controlRevision;
        public long playerRevision;
        public float bufferSeconds;
        public bool pending;
        internal ArdyRoomStreamHandoff Copy() => (ArdyRoomStreamHandoff)MemberwiseClone();
    }

    public sealed partial class ArdyRoomLocomotionController
    {
        [Serializable] private sealed class ServiceStreamDiagnostic
        {
            public string stage;
            public float elapsedMs;
            public int deadlineMs;
        }
        [Serializable] private sealed class ServiceErrorDetails { public ServiceStreamDiagnostic streamDiagnostic; }
        [Serializable] private sealed class ServiceErrorBody { public string code; public ServiceErrorDetails details; }
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
            public readonly string Response;
            public readonly long HttpStatus;
            public ServiceRequestException(string message, string response) : this(message, response, 0) { }
            public ServiceRequestException(string message, string response, long httpStatus) : base(message + DescribeServiceFailure(response))
            {
                Response = response; HttpStatus = httpStatus;
                // Error bodies can be empty or non-JSON (proxy/transport failures).
                // Only the machine-readable code is used for diagnosis, never execution.
                try { if (!string.IsNullOrEmpty(response)) Code = JsonUtility.FromJson<ServiceErrorEnvelope>(response)?.error?.code; }
                catch (ArgumentException) { }
            }
        }
        [Serializable] private sealed class StreamTimings
        {
            public float totalMs, backendMs, backendGateMs, queueMs, featureMs;
            public int qualityAttempts;
        }
        private string streamEventName, streamLastFailureCategory, streamLastFailureReason;
        private string streamFailureRequestId, streamFailureSessionId, streamFailureObservedUtc, streamFailureStage;
        private ArdyRoomStreamHandoff streamLastHandoff;
        private sealed class StreamLifecycleException : InvalidOperationException
        {
            public readonly ArdyRoomStreamHandoff Evidence;
            public StreamLifecycleException(string message, ArdyRoomStreamHandoff evidence) : base(message) { Evidence = evidence; }
        }
        private ArdyRoomStreamHandoff CaptureStreamHandoff(string reason) => new ArdyRoomStreamHandoff {
            reason = reason, sessionId = streamSessionId, requestId = requestId, stage = Stage,
            observedUtc = DateTime.UtcNow.ToString("o"), operationRevision = OperationRevision,
            wireRevision = streamWireRevision, controlRevision = StreamControlRevision,
            playerRevision = ownedPlayerRevision, bufferSeconds = StreamBufferSeconds, pending = streamRequestPending
        };
        private void RecordStreamHandoff(string reason)
        {
            streamLastHandoff = CaptureStreamHandoff(reason);
            Debug.Log("[ARDY room/handoff] " + JsonUtility.ToJson(streamLastHandoff), this);
        }
        private int streamFailureOperationRevision = -1;
        private float streamRoundTrip, streamBackend, streamBackendQueue, streamFeature, streamPlanning, streamParse, streamAudit;
        private float streamRecentFramePeak, streamRecentStepCost;
        private bool streamRequestPending;

        public ArdyRoomStreamTelemetry StreamTelemetry => new ArdyRoomStreamTelemetry {
            eventName = streamEventName, requestId = requestId, sessionId = streamSessionId, stage = Stage,
            lastFailureCategory = streamLastFailureCategory, lastFailureReason = streamLastFailureReason,
            failureRequestId = streamFailureRequestId, failureSessionId = streamFailureSessionId,
            failureObservedUtc = streamFailureObservedUtc, failureOperationRevision = streamFailureOperationRevision,
            failureStage = streamFailureStage, lastHandoff = streamLastHandoff?.Copy(),
            failureAppliesToCurrentOperation = streamFailureOperationRevision == OperationRevision &&
                !string.IsNullOrEmpty(streamLastFailureCategory),
            historyCode = warmHistoryObservation?.code, historyReason = warmHistoryObservation?.reason,
            latestRejectedPrefixOrdinal = LatestRejectedPrefixOrdinal,
            operationRevision = OperationRevision, stepIndex = streamStepIndex, controlRevision = StreamControlRevision,
            recoveryCount = StreamRecoveryCount, consecutiveRecoveries = streamConsecutiveRecoveries,
            qualityRecoveryCount = StreamQualityRecoveryCount, qualityRecoveryLimit = StreamQualityRecoveryLimit,
            qualityFailureEvidencePath = LastQualityFailureEvidencePath,
            controlRecoveryCount = StreamControlRecoveryCount, controlRecoveryLimit = StreamControlRecoveryLimit,
            controlFailureEvidencePath = LastControlFailureEvidencePath, controlPlanningMode = streamControlPath.LastPlanningMode,
            warmContinuationReason = streamControlPath.LastWarmExitReason, controlHardInitialSpeed = streamControlPath.LastHardInitialSpeed,
            controlNominalInitialSpeed = streamControlPath.LastNominalInitialSpeed, controlTrackingError = streamControlPath.LastTrackingError,
            controlRemainingDistance = streamControlPath.LastRemainingDistance,
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
            => RecordStreamEventWithEvidence(name, reason, category, null);

        private void RecordStreamEventWithEvidence(string name, string reason, string category, ArdyRoomStreamHandoff evidence)
        {
            streamEventName = name;
            if (reason != null) {
                streamLastFailureReason = reason; streamLastFailureCategory = category;
                streamFailureRequestId = evidence != null ? evidence.requestId : requestId;
                streamFailureSessionId = evidence != null ? evidence.sessionId : streamSessionId;
                streamFailureStage = evidence != null ? evidence.stage : Stage;
                streamFailureOperationRevision = evidence != null ? evidence.operationRevision : OperationRevision;
                streamFailureObservedUtc = DateTime.UtcNow.ToString("o");
            }
            Debug.Log("[ARDY room/stream] " + JsonUtility.ToJson(StreamTelemetry), this);
        }

        private static string StreamFailureCategory(Exception error)
        {
            if (error.InnerException is ArdyRoomStreamTrajectory.KinematicsException) return "motion-control";
            if (error is PoseHistoryException) return "pose-history";
            var execution = error as ExecutionException;
            if ((error as ServiceRequestException)?.Code == "stream_quality_rejected") return "motion-quality";
            if (execution?.Reason == ArdyRoomExecutionReason.OwnershipLost) return "ownership";
            if (error.Message.Contains("净距")) return "user-clearance";
            if (error.Message.Contains("步态") || error.Message.Contains("关节") || error.Message.Contains("stream_quality_rejected")) return "motion-quality";
            if (execution?.Reason == ArdyRoomExecutionReason.NavigationRejected || execution?.Reason == ArdyRoomExecutionReason.GeneratedPathRejected)
                return "navigation";
            if (error is ServiceRequestException) return "service-request";
            if (error is StreamLifecycleException) return "stream-lifecycle";
            return "client-state";
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
                        bool recovering = false;
                        try { recovering = TryPrepareQualityRecovery(failure, token) || TryPrepareControlRecovery(failure, token); }
                        catch (Exception recoveryFailure) { failure = recoveryFailure; }
                        if (recovering)
                        {
                            (routine as IDisposable)?.Dispose();
                            routine = RunContinuousStream(token);
                            yield return null;
                            continue;
                        }
                        RecordStreamEventWithEvidence("failed", failure.Message, StreamFailureCategory(failure), (failure as StreamLifecycleException)?.Evidence);
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
