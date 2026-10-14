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
        public int StreamControlRecoveryCount { get; private set; }
        public const int StreamControlRecoveryLimit = 1;
        public string LastControlFailureEvidencePath { get; private set; }
        private int controlFailureOperation = -1;
        private string controlFailureRequest;

        private bool TryPrepareControlRecovery(Exception failure, int token)
        {
            // Only an identified local kinematic rejection may take this path.
            // Obstacle, clearance, service, quality and ownership failures retain
            // their own policies. No rejected future frame is ever played.
            if (!(failure.InnerException is ArdyRoomStreamTrajectory.KinematicsException cause) ||
                cause.Code != "braking-distance" || !Current(token) || token != OperationRevision ||
                controlFailureOperation != token || controlFailureRequest != requestId ||
                !IsContinuousFollowActive || Stage != "stream-control" || streamStepIndex < 1 ||
                StreamControlRecoveryCount >= StreamControlRecoveryLimit || streamConsecutiveRecoveries >= StreamRecoveryLimit ||
                avatar == null || navigation == null || player == null || AvatarUnavailableReason() != null ||
                ownedClip == null || player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision ||
                !player.HasPoseOwnership) return false;
            // Recheck the actual root and current target; a former good route is
            // not evidence that a new route remains reachable after an obstruction.
            if (!navigation.TryPlan(AvatarGround(), TargetGround, out _, out _)) return false;
            StreamControlRecoveryCount++;
            bool prepared = PrepareStreamRecovery("续段制动约束不满足，停止已审核位移后，从新的实际姿态重新检查当前目标。",
                "motion-control", "control-recovery", false);
            if (prepared) warmStreamPoses.Clear();
            return prepared;
        }

        private void SaveControlFailure(Exception error, ArdyRoomRoute route, Vector3 source,
            float heading, float speed, float terminalSpeed, Vector3 seam)
        {
            controlFailureOperation = OperationRevision; controlFailureRequest = requestId;
            string path = null, saveFailure = null;
            try
            {
                JToken Value<T>(T value) => JToken.Parse(JsonUtility.ToJson(value));
                var old = streamControlPath.path;
                var evidence = new JObject {
                    ["version"] = "stream-control-failure-v1", ["observedUtc"] = DateTime.UtcNow.ToString("o"),
                    ["requestId"] = requestId, ["sessionId"] = streamSessionId, ["operationRevision"] = OperationRevision,
                    ["stepIndex"] = streamStepIndex, ["controlRevision"] = StreamControlRevision,
                    ["requestSubmitted"] = false, ["error"] = error.Message,
                    ["code"] = (error as ArdyRoomStreamTrajectory.KinematicsException)?.Code,
                    ["telemetry"] = Value(StreamTelemetry), ["actualRoot"] = Value(avatar.transform.position),
                    ["sourcePosition"] = Value(source), ["sourceHeading"] = heading, ["initialSpeed"] = speed,
                    ["desiredSpeed"] = StreamDesiredSpeed, ["terminalSpeed"] = terminalSpeed,
                    ["anchorGround"] = Value(anchorGround), ["anchorRotation"] = Value(anchorRotation),
                    ["rootOffset"] = Value(neutralHorizontal), ["motionScale"] = motionScale,
                    ["seamGround"] = Value(seam), ["targetGround"] = Value(TargetGround),
                    ["routeCorners"] = new JArray(route.Corners.Select(v => Value(v))),
                    ["continuity"] = new JObject {
                        ["resumeDistance"] = streamControlPath.resumeDistance,
                        ["anchorRotation"] = Value(streamControlPath.anchorRotation),
                        ["committedSpeed"] = streamControlPath.CommittedSpeed,
                        ["truncated"] = old != null && old.Count > 4096,
                        ["knots"] = old == null ? new JArray() : new JArray(old.Take(4096).Select(k => new JObject {
                            ["point"] = Value(k.point), ["heading"] = k.heading, ["speed"] = k.speed,
                            ["distance"] = k.distance, ["time"] = k.time, ["stop"] = k.stop })) },
                    ["lastAcceptedRequest"] = ParseDiagnosticJson(segmentEvidence.LastOrDefault(e => !string.IsNullOrEmpty(e.responseJson))?.requestJson),
                    ["lastAcceptedResponse"] = ParseDiagnosticJson(segmentEvidence.LastOrDefault(e => !string.IsNullOrEmpty(e.responseJson))?.responseJson),
                    ["scope"] = "Local control rejection before HTTP. Saved native seam, previous control path and failure constraints permit numerical replay; room clearance still requires the original live geometry. No replay or retry permission."
                };
#if UNITY_EDITOR
                string root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Server", "ARDY", "runtime", "room-control-failures");
#else
                string root = Path.Combine(Application.persistentDataPath, "ARDY", "room-control-failures");
#endif
                Directory.CreateDirectory(root);
                path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + requestId + ".json");
                File.WriteAllText(path, evidence.ToString(Formatting.Indented));
                LastControlFailureEvidencePath = path;
            }
            catch (Exception e) { saveFailure = e.GetType().Name + ": " + e.Message; }
            Debug.Log("[ARDY room/control] " + new JObject {
                ["operationRevision"] = OperationRevision, ["requestId"] = requestId,
                ["planningMode"] = streamControlPath.LastPlanningMode, ["warmExitReason"] = streamControlPath.LastWarmExitReason,
                ["initialSpeed"] = speed, ["hardInitialSpeed"] = streamControlPath.LastHardInitialSpeed,
                ["trackingError"] = streamControlPath.LastTrackingError, ["remainingDistance"] = streamControlPath.LastRemainingDistance,
                ["recoveriesUsed"] = StreamControlRecoveryCount, ["recoveryLimit"] = StreamControlRecoveryLimit,
                ["evidencePath"] = path, ["saveFailure"] = saveFailure }.ToString(Formatting.None), this);
        }
    }
}
