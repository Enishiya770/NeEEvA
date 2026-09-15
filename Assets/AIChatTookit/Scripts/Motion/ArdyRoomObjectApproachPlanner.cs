using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyRoomObjectApproachObservation
    {
        public string stage = "setup", code = "", reason = "";
        public Vector3 visibleSurfacePoint, plannedGround;
        public bool success;
        public int candidateCount, acceptedCount, navigationRejected, userClearanceRejected, selectedCandidate = -1;
        public ArdyRoomRouteDiagnostic preflightFailure;
        public ArdyRoomUserClearanceObservation userClearance;
        public List<ArdyRoomApproachCandidateObservation> candidates = new List<ArdyRoomApproachCandidateObservation>(24);
        public string scope = "Bounded same-level floor stations near a visually grounded surface. Surface height is never a foot destination. Geometry is engine validation, not a semantic identification or proof of arrival.";
    }

    [Serializable]
    public sealed class ArdyRoomUserClearanceObservation
    {
        public bool valid;
        public Vector3 trackedHead, center;
        public string reason = "";
        public string scope = "Current valid tracked user XZ projected onto the actor route plane only for conservative body clearance. This is not a measured user foot position or a claim of user floor support.";
    }

    public sealed class ArdyRoomObjectApproachPlan
    {
        public Vector3 GroundTarget { get; internal set; }
        public Vector3 FacePoint { get; internal set; }
        public Vector3 UserClearanceCenter { get; internal set; }
        public float UserClearanceRadius { get; internal set; }
        public ArdyRoomRoute Route { get; internal set; }
        public ArdyRoomObjectApproachObservation Observation { get; internal set; }
    }

    /// <summary>Plans a legal floor station beside a seen surface; never navigates onto the object.</summary>
    public static class ArdyRoomObjectApproachPlanner
    {
        /// <summary>
        /// Object-directed movement reserves the explicit user's horizontal body disk even when the
        /// desktop/VR view is over furniture, airborne or on another level. User floor support is not
        /// relevant to the actor's goal. This motion assembly validates the Transform; the bridge
        /// additionally validates PlayerCameraController tracking before planning and before each step.
        /// </summary>
        public static bool TryGetUserClearanceCenter(Transform userAnchor, float actorGroundY,
            out Vector3 center, out string reason)
        {
            center = default;
            reason = null;
            if (userAnchor == null || !userAnchor.gameObject.activeInHierarchy ||
                !userAnchor.gameObject.scene.IsValid() || !userAnchor.gameObject.scene.isLoaded ||
                !ArdyLocomotionClip.Finite(userAnchor.position) || float.IsNaN(actorGroundY) || float.IsInfinity(actorGroundY))
                reason = "用户身体安全锚点无效，不能规划或继续空间行动。";
            else center = new Vector3(userAnchor.position.x, actorGroundY, userAnchor.position.z);
            return reason == null;
        }

        public static bool TryPlan(ArdyRoomNavigation navigation, Vector3 avatarGround,
            Vector3 visibleSurfacePoint, Transform userAnchor, float standOff,
            out ArdyRoomObjectApproachPlan result, out string reason, out ArdyRoomObjectApproachObservation observation)
        {
            result = null;
            observation = new ArdyRoomObjectApproachObservation { visibleSurfacePoint = visibleSurfacePoint };
            if (navigation == null || !navigation.isActiveAndEnabled || !navigation.IsBuilt)
                return Fail(observation, "navigation-unavailable", "房间导航尚未准备好。", out reason);
            if (!ArdyLocomotionClip.Finite(avatarGround) || !ArdyLocomotionClip.Finite(visibleSurfacePoint) ||
                float.IsNaN(standOff) || float.IsInfinity(standOff) || standOff < .5f || standOff > 2f)
                return Fail(observation, "invalid-input", "视觉目标坐标或站位距离无效。", out reason);
            observation.stage = "start";
            if (!navigation.TryValidatePlanStart(avatarGround, out Vector3 start, out reason, out var startObservation))
            {
                observation.preflightFailure = startObservation;
                return Fail(observation, startObservation.code, reason, out reason);
            }
            observation.stage = "user-clearance-anchor";
            observation.userClearance = new ArdyRoomUserClearanceObservation {
                trackedHead = userAnchor != null ? userAnchor.position : Vector3.zero };
            if (!TryGetUserClearanceCenter(userAnchor, start.y, out Vector3 userCenter, out reason))
            {
                observation.userClearance.reason = reason;
                return Fail(observation, "user-clearance-anchor-unavailable", reason, out reason);
            }
            observation.userClearance.valid = true;
            observation.userClearance.center = userCenter;
            float radius = navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
            Vector3 center = new Vector3(visibleSurfacePoint.x, start.y, visibleSurfacePoint.z);
            Vector3 radial = start - center;
            radial.y = 0;
            // Only chooses the bounded search order when already horizontally coincident.
            Vector3 preferred = radial.sqrMagnitude > .000001f ? radial.normalized : Vector3.forward;
            float distance = Mathf.Max(standOff, navigation.AgentRadius + .25f);
            if (radial.magnitude >= distance - .15f && radial.magnitude <= distance + .15f &&
                navigation.TryPlan(start, start, out var stationary, out _) &&
                ArdyRoomApproachPlanner.RouteKeepsUserClearance(stationary, userCenter, radius))
            {
                observation.stage = "completed"; observation.success = true; observation.plannedGround = start;
                observation.reason = "当前已位于可用的物体邻近地面站位；仅需确认朝向。";
                result = new ArdyRoomObjectApproachPlan { GroundTarget = start, FacePoint = visibleSurfacePoint,
                    UserClearanceCenter = userCenter, UserClearanceRadius = radius, Route = stationary, Observation = observation };
                reason = null; return true;
            }
            ArdyRoomRoute best = null;
            float bestScore = float.PositiveInfinity;
            int[] turns = { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6 };
            observation.stage = "candidates";
            for (int ring = 0; ring < 2; ring++)
            {
                foreach (int turn in turns)
                {
                    Vector3 point = center + Quaternion.AngleAxis(turn * 30f, Vector3.up) * preferred * (distance + ring * .30f);
                    var candidate = new ArdyRoomApproachCandidateObservation {
                        index = observation.candidateCount++, ring = ring, turn = turn, candidate = point };
                    observation.candidates.Add(candidate);
                    if (!navigation.TryPlan(start, point, out var route, out _, out var detail))
                    { observation.navigationRejected++; candidate.navigation = detail; continue; }
                    candidate.navigation = detail;
                    candidate.routeLength = route.Length;
                    if (!ArdyRoomApproachPlanner.RouteKeepsUserClearance(route, userCenter, radius))
                    {
                        observation.userClearanceRejected++;
                        candidate.navigation = new ArdyRoomRouteDiagnostic { stage = "user-clearance", code = "user-clearance",
                            start = start, target = point, reason = "此路线会穿过用户身体周围的保留空间。" };
                        continue;
                    }
                    candidate.accepted = true;
                    observation.acceptedCount++;
                    float score = route.Length + Mathf.Abs(turn) * .025f + ring * .12f;
                    if (score >= bestScore) continue;
                    best = route; bestScore = score; observation.selectedCandidate = candidate.index;
                }
            }
            if (best == null)
                return Fail(observation, "bounded-candidates-rejected",
                    $"本次检查的 {observation.candidateCount} 个物体邻近地面站位均未通过：导航拒绝 {observation.navigationRejected}，用户身体间距拒绝 {observation.userClearanceRejected}。这是有限搜索结果；没有尝试登上物体或跨越高差。", out reason);
            observation.stage = "completed"; observation.success = true;
            observation.plannedGround = best.Corners[best.Corners.Length - 1];
            observation.reason = "已找到物体附近的同层地面站位；尚未执行或确认到达。";
            result = new ArdyRoomObjectApproachPlan { GroundTarget = observation.plannedGround,
                FacePoint = visibleSurfacePoint, UserClearanceCenter = userCenter, UserClearanceRadius = radius,
                Route = best, Observation = observation };
            reason = null; return true;
        }

        private static bool Fail(ArdyRoomObjectApproachObservation observation, string code, string text, out string reason)
        { observation.code = code; observation.reason = reason = text; return false; }
    }
}
