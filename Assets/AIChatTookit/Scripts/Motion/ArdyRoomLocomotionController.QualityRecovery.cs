using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyRoomLocomotionController
    {
        public int StreamQualityRecoveryCount { get; private set; }
        public const int StreamQualityRecoveryLimit = 1; // Lifetime of the parent operation, never replenished by healthy playback.
        public string LastQualityFailureEvidencePath { get; private set; }

        private bool TryPrepareQualityRecovery(Exception error, int token)
        {
            var service = error as ServiceRequestException;
            if (service?.Code != "stream_quality_rejected") return false;
            var details = ArdyStreamQualityFailure.Read(service.Response);
            SaveQualityFailure(details, service);
            if (service.HttpStatus != 422 || !ArdyStreamQualityFailure.SupportsFreshHistoryRecovery(details) ||
                !Current(token) || token != OperationRevision || !IsContinuousFollowActive ||
                StreamQualityRecoveryCount >= StreamQualityRecoveryLimit || streamConsecutiveRecoveries >= StreamRecoveryLimit ||
                string.IsNullOrEmpty(streamSessionId) || avatar == null || navigation == null || player == null ||
                AvatarUnavailableReason() != null) return false;
            var request = JsonUtility.FromJson<StreamRequest>(LastRequestJson);
            if (request == null || request.requestId != requestId || request.sessionId != streamSessionId ||
                request.characterId != characterId || request.revision != streamWireRevision || request.stepIndex != streamStepIndex)
                return false;
            if (ownedClip == null) CheckContinuousStreamPlayerOwnership();
            else if (player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision || !player.HasPoseOwnership)
                throw new ExecutionException("质量拒绝后身体归属已改变，不能恢复旧跟随。", ArdyRoomExecutionReason.OwnershipLost);
            if (!navigation.TryPlan(AvatarGround(), TargetGround, out _, out string reason))
                throw new ExecutionException("质量拒绝后当前目标不可达，不能恢复 · " + reason, ArdyRoomExecutionReason.NavigationRejected);
            StreamQualityRecoveryCount++;
            // Stop the accepted prefix, cancel the old native session and observe
            // a new rendered history. Never retry its stale request or adopt its tail.
            bool prepared = PrepareStreamRecovery("动作候选未通过质量审核，已停止位移；正在从新的实际姿态有限恢复。",
                "motion-quality", "quality-recovery", false);
            if (prepared) warmStreamPoses.Clear();
            return prepared;
        }

        private void SaveQualityFailure(JObject details, ServiceRequestException error)
        {
            var evidence = new JObject {
                ["version"] = "stream-quality-failure-v1", ["observedUtc"] = DateTime.UtcNow.ToString("o"),
                ["operationRevision"] = OperationRevision, ["requestId"] = requestId,
                ["sessionId"] = streamSessionId, ["wireRevision"] = streamWireRevision,
                ["stepIndex"] = streamStepIndex, ["currentControlRevision"] = StreamControlRevision,
                ["acceptedControlRevision"] = StreamAcceptedControlRevision, ["appliedControlRevision"] = StreamAppliedControlRevision,
                ["qualityRecoveriesUsed"] = StreamQualityRecoveryCount, ["qualityRecoveryLimit"] = StreamQualityRecoveryLimit,
                ["httpStatus"] = error.HttpStatus, ["error"] = details,
                ["request"] = ParseDiagnosticJson(LastRequestJson), ["rawErrorResponse"] = error.Response,
                ["avatarInstance"] = avatar == null ? 0 : avatar.GetInstanceID(),
                ["playerRevision"] = player == null ? -1 : player.PlaybackRevision,
                ["actualRoot"] = avatar == null ? null : JObject.Parse(JsonUtility.ToJson(avatar.transform.position)),
                ["actualRotation"] = avatar == null ? null : JObject.Parse(JsonUtility.ToJson(avatar.transform.rotation)),
                ["renderedPoseHistory"] = new JArray(warmStreamPoses.Select(p => new JObject {
                    ["time"] = p.time, ["root"] = JObject.Parse(JsonUtility.ToJson(p.root)),
                    ["hips"] = JObject.Parse(JsonUtility.ToJson(p.hips)), ["rotation"] = JObject.Parse(JsonUtility.ToJson(p.rotation)),
                    ["scale"] = p.scale, ["parentInstance"] = p.parent == null ? 0 : p.parent.GetInstanceID(),
                    ["worldRotations"] = new JArray(p.worldRotations.Select(q => JObject.Parse(JsonUtility.ToJson(q)))) })),
                ["previousAcceptedSegment"] = ParseDiagnosticJson(segmentEvidence.LastOrDefault(e =>
                    !string.IsNullOrEmpty(e.responseJson))?.responseJson),
                ["scope"] = "Captured before recovery/cancellation. Rendered history and previous accepted response are evidence, not permission to replay. Native hidden history is not reconstructed here."
            };
            string path = null, saveFailure = null;
            try
            {
#if UNITY_EDITOR
                string root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Server", "ARDY", "runtime", "room-quality-failures");
#else
                string root = Path.Combine(Application.persistentDataPath, "ARDY", "room-quality-failures");
#endif
                Directory.CreateDirectory(root);
                path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, evidence.ToString(Formatting.Indented));
                LastQualityFailureEvidencePath = path;
            }
            catch (Exception failure) { saveFailure = failure.GetType().Name + ": " + failure.Message; }
            Debug.Log("[ARDY room/quality] " + new JObject {
                ["requestId"] = requestId, ["sessionId"] = streamSessionId, ["operationRevision"] = OperationRevision,
                ["stepIndex"] = streamStepIndex, ["error"] = details, ["evidencePath"] = path,
                ["saveFailure"] = saveFailure, ["qualityRecoveriesUsed"] = StreamQualityRecoveryCount,
                ["qualityRecoveryLimit"] = StreamQualityRecoveryLimit }.ToString(Formatting.None), this);
        }

        private static JToken ParseDiagnosticJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try { return JToken.Parse(value); } catch (JsonException) { return new JObject { ["unparsed"] = value }; }
        }
    }
}
