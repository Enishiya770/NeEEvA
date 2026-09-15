using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable] public sealed class ArdyRoomPoseHistoryObservation
    {
        public bool success;
        public string code, reason, observedUtc;
        public int operationRevision, sampleCount, sampleIndex = -1;
        public float realtime, spanSeconds, latestAgeSeconds, measured, limit;
    }

    [Serializable] public sealed class ArdyRoomRejectedPrefixEvidence
    {
        public int schema = 1, ordinal, operationRevision, wireRevision, stepIndex, controlRevision;
        public int candidateFrame = -1, requestFrame = -1, globalFrame = -1, targetIndex = -1, requestedFrames, commitFrames;
        public string capturedUtc, stage, checkPhase, category, reason, characterId, requestId, sessionId, previousRequestId, clipId;
        public float realtime, motionScale, sourceRootHeight, floorY, routeLength, routeGroundY;
        public bool hasCandidateFrame, routeTruncated, payloadIdentityMatched, requestSubmitted, responseAvailable;
        public Vector3 previousWorldRoot, rejectedWorldRoot, actualWorldRoot, sourceRoot, targetGround, userGround,
            anchorRootPosition, anchorGround, neutralHorizontal;
        public Quaternion anchorRotation;
        public Vector3[] routeCorners;
        public ArdyRoomRouteDiagnostic navigation;
        public string requestJson, responseJson, payloadOmission;
        public int rawUtf8Bytes;
    }

    public sealed partial class ArdyRoomLocomotionController
    {
        public const int MaximumRejectedPrefixes = 4, MaximumRejectedRawUtf8Bytes = 4 * 1024 * 1024;
        public const int MaximumRejectedSinglePayloadBytes = 1024 * 1024;
        private readonly List<ArdyRoomRejectedPrefixEvidence> rejectedPrefixes = new List<ArdyRoomRejectedPrefixEvidence>();
        private int rejectedPrefixOrdinal, rejectedRawBytes;
        private ArdyRoomPoseHistoryObservation warmHistoryObservation, rejectedHistoryObservation;
        private string warmHistoryCaptureError;
        public ArdyRoomPoseHistoryObservation StreamHistoryObservation => CopyEvidence(warmHistoryObservation);
        public ArdyRoomPoseHistoryObservation LastRejectedStreamHistory => CopyEvidence(rejectedHistoryObservation);
        public int LatestRejectedPrefixOrdinal => rejectedPrefixes.Count == 0 ? 0 : rejectedPrefixes[rejectedPrefixes.Count - 1].ordinal;

        // Detached copies prevent a diagnostic viewer from modifying the retained evidence.
        public ArdyRoomRejectedPrefixEvidence[] ReadRecentRejectedPrefixes()
        {
            var result = new ArdyRoomRejectedPrefixEvidence[rejectedPrefixes.Count];
            for (int i = 0; i < result.Length; i++) result[i] = CopyEvidence(rejectedPrefixes[i]);
            return result;
        }
        private static T CopyEvidence<T>(T value) where T : class => value == null ? null : JsonUtility.FromJson<T>(JsonUtility.ToJson(value));

        private sealed class PoseHistoryException : InvalidOperationException
        {
            public readonly string Code;
            public PoseHistoryException(string code, string message) : base(message) { Code = code; }
        }
        private bool RejectWarmHistory(string code, string reason, int index = -1, float measured = 0, float limit = 0)
        {
            float now = Time.realtimeSinceStartup;
            warmHistoryObservation = new ArdyRoomPoseHistoryObservation { code = code, reason = reason,
                observedUtc = DateTime.UtcNow.ToString("O"), realtime = now, operationRevision = OperationRevision,
                sampleCount = warmStreamPoses.Count, sampleIndex = index, measured = measured, limit = limit,
                spanSeconds = warmStreamPoses.Count < 2 ? 0 : warmStreamPoses[warmStreamPoses.Count - 1].time - warmStreamPoses[0].time,
                latestAgeSeconds = warmStreamPoses.Count == 0 ? 0 : now - warmStreamPoses[warmStreamPoses.Count - 1].time };
            return false;
        }
        private PoseHistoryException CreatePoseHistoryException()
        {
            if (warmHistoryObservation == null || warmHistoryObservation.success)
                RejectWarmHistory("history-unavailable", "没有取得可用的连续实际姿态历史。");
            rejectedHistoryObservation = CopyEvidence(warmHistoryObservation);
            return new PoseHistoryException(warmHistoryObservation.code,
                "本地实际姿态历史未就绪 · " + warmHistoryObservation.reason + "（" + warmHistoryObservation.code + "）");
        }

        private string GuardStepDetailed(Vector3 previous, Vector3 next, out ArdyRoomRouteDiagnostic observation,
            bool includeAdditionalGuard = true)
        {
            observation = new ArdyRoomRouteDiagnostic { stage = "execution-step", start = previous, target = next,
                from = previous, to = next };
            if (navigation == null) { observation.code = "navigation-unavailable"; return observation.reason = "房间导航已离开"; }
            string avatarReason = AvatarUnavailableReason();
            if (avatarReason != null) { observation.code = "avatar-unavailable"; return observation.reason = avatarReason; }
            Vector3 a = previous, b = next; a.y = b.y = floorY;
            string reason;
            bool admitted;
            if (boundaryRecovery != null)
            {
                admitted = navigation.ValidateBoundaryRecoveryStep(a, b, boundaryRecovery, out _, out reason);
                observation.code = admitted ? "" : "boundary-recovery-rejected";
                observation.reason = reason; // No second geometry query or invented collider identity.
            }
            else admitted = navigation.ValidateStep(a, b, out _, out reason, out observation);
            if (!admitted) return reason;
            reason = includeAdditionalGuard ? AdditionalStepGuard?.Invoke(previous, next) : null;
            observation.success = reason == null;
            if (reason != null)
            {
                observation.stage = "additional-step-guard";
                observation.code = reason.Contains("净距") ? "user-clearance" : "additional-step-rejected";
                observation.reason = reason;
                // AdditionalStepGuard returns text only. Its own bridge observation contains the
                // actual nearest point; do not claim the segment endpoint is that checked point.
                observation.hasSample = false;
            }
            return reason;
        }

        private ExecutionException RejectGeneratedPrefix(ArdyLocomotionClip candidate, int candidateFrame,
            Vector3 previous, Vector3 next, string reason, ArdyRoomRouteDiagnostic observation = null,
            string category = null, SegmentEvidence evidence = null, ArdyRoomRoute checkedRoute = null,
            string checkPhase = "committed-prefix", int targetIndex = -1)
        {
            var error = new ExecutionException(reason, ArdyRoomExecutionReason.GeneratedPathRejected);
            try
            {
                int globalFrame = candidateFrame < 0 ? -1 : candidateFrame +
                    (evidence != null ? evidence.startFrame : streamBaseFrame);
                if (evidence == null && candidate != null && candidateFrame >= 0)
                    for (int i = segmentEvidence.Count - 1; i >= 0; i--)
                    {
                        var item = segmentEvidence[i];
                        if (item.operationRevision == OperationRevision && item.wireRevision == streamWireRevision &&
                            globalFrame >= item.startFrame && globalFrame < item.startFrame + item.auditedFrames)
                        { evidence = item; break; }
                    }
                string rawRequest = evidence?.requestJson ?? (candidate == null ? LastRequestJson : null);
                string rawResponse = evidence?.responseJson;
                StreamRequest request = string.IsNullOrEmpty(rawRequest) ? null : JsonUtility.FromJson<StreamRequest>(rawRequest);
                bool matched = request != null && request.characterId == characterId && request.sessionId == streamSessionId &&
                    request.revision == streamWireRevision && (evidence == null || request.requestId == evidence.requestId);
                if (matched && rawResponse != null)
                {
                    var responseIdentity = JsonUtility.FromJson<StreamResponse>(rawResponse);
                    matched = responseIdentity != null && responseIdentity.characterId == request.characterId &&
                        responseIdentity.sessionId == request.sessionId && responseIdentity.requestId == request.requestId &&
                        responseIdentity.revision == request.revision && responseIdentity.startFrame == request.startFrame &&
                        responseIdentity.stepIndex == request.stepIndex && responseIdentity.controlRevision == request.controlRevision;
                }
                bool hasFrame = candidate != null && candidateFrame >= 0 && candidateFrame < candidate.frames.Length;
                var route = checkedRoute ?? Route;
                var record = new ArdyRoomRejectedPrefixEvidence { ordinal = ++rejectedPrefixOrdinal,
                    capturedUtc = DateTime.UtcNow.ToString("O"), realtime = Time.realtimeSinceStartup,
                    operationRevision = OperationRevision, wireRevision = streamWireRevision, stepIndex = request?.stepIndex ?? streamStepIndex,
                    controlRevision = request?.controlRevision ?? StreamControlRevision, stage = Stage, checkPhase = checkPhase,
                    category = category ?? StreamFailureCategory(error), reason = reason, characterId = characterId,
                    requestId = matched ? request.requestId : null, sessionId = streamSessionId,
                    previousRequestId = matched ? request.previousRequestId : null, clipId = candidate?.id,
                    candidateFrame = candidateFrame, globalFrame = globalFrame, targetIndex = targetIndex,
                    requestFrame = matched && globalFrame >= 0 ? globalFrame - request.startFrame : -1,
                    requestedFrames = request?.targets?.Length ?? candidate?.frames.Length ?? 0, commitFrames = request?.commitFrames ?? 0,
                    requestSubmitted = !checkPhase.StartsWith("control-", StringComparison.Ordinal) && evidence != null && evidence.completedAtRealtime > 0,
                    responseAvailable = rawResponse != null,
                    payloadIdentityMatched = matched, hasCandidateFrame = hasFrame, previousWorldRoot = previous,
                    rejectedWorldRoot = next, actualWorldRoot = avatar == null ? default : avatar.transform.position,
                    sourceRoot = hasFrame ? candidate.frames[candidateFrame].rootPosition : default,
                    targetGround = TargetGround, userGround = streamUserGround, anchorRootPosition = anchorRootPosition,
                    anchorGround = anchorGround, anchorRotation = anchorRotation, neutralHorizontal = neutralHorizontal,
                    motionScale = motionScale, sourceRootHeight = sourceRootHeight, floorY = floorY,
                    routeLength = route?.Length ?? 0, routeGroundY = route?.GroundY ?? 0, navigation = CopyEvidence(observation) };
                int corners = Mathf.Min(route?.Corners?.Length ?? 0, 256);
                record.routeCorners = new Vector3[corners];
                if (corners > 0) Array.Copy(route.Corners, record.routeCorners, corners);
                record.routeTruncated = (route?.Corners?.Length ?? 0) > corners;
                if (!matched) record.payloadOmission = "No request with matching operation/wire/session/frame identity was available; no unrelated last response substituted.";
                else
                {
                    int requestBytes = Encoding.UTF8.GetByteCount(rawRequest ?? ""), responseBytes = Encoding.UTF8.GetByteCount(rawResponse ?? "");
                    if (requestBytes > MaximumRejectedSinglePayloadBytes || responseBytes > MaximumRejectedSinglePayloadBytes)
                        record.payloadOmission = "A raw payload exceeded the 1 MiB per-payload limit; exact metadata retained, raw JSON omitted as a pair.";
                    else
                    {
                        record.requestJson = rawRequest; record.responseJson = rawResponse;
                        record.rawUtf8Bytes = requestBytes + responseBytes;
                        if (rawResponse == null) record.payloadOmission = "No native response exists for this preflight rejection; the request was not dispatched.";
                    }
                }
                while (rejectedPrefixes.Count > 0 && (rejectedPrefixes.Count >= MaximumRejectedPrefixes ||
                    rejectedRawBytes + record.rawUtf8Bytes > MaximumRejectedRawUtf8Bytes))
                { rejectedRawBytes -= rejectedPrefixes[0].rawUtf8Bytes; rejectedPrefixes.RemoveAt(0); }
                rejectedPrefixes.Add(record); rejectedRawBytes += record.rawUtf8Bytes;
                if (evidence != null) evidence.outcome = "stream-rejected";
            }
            catch (Exception diagnosticError)
            {
                // The original safety rejection remains authoritative if evidence collection fails.
                lastRejectedEvidenceError = diagnosticError.GetType().Name + ": " + diagnosticError.Message;
            }
            return error;
        }
        private string lastRejectedEvidenceError;
        public string LastRejectedEvidenceError => lastRejectedEvidenceError;
    }
}
