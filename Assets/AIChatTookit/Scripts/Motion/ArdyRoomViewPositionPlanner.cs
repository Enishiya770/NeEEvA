using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>A single explicit offset relative to a confirmed visible target. No alternate goal search.</summary>
    public static class ArdyRoomViewPositionPlanner
    {
        public const float MinimumDistance = .2f, MaximumDistance = 1.2f, MaximumTargetDistance = 8f;
        public const float CorridorTolerance = .08f;

        public static bool TryOffset(Vector3 start, Vector3 target, string direction, float distance,
            float minimumTargetDistance, out Vector3 ground, out string reason)
        {
            ground = start; reason = null;
            if (!ArdyLocomotionClip.Finite(start) || !ArdyLocomotionClip.Finite(target) ||
                !ArdyLocomotionClip.Finite(distance) || distance < MinimumDistance || distance > MaximumDistance ||
                !ArdyLocomotionClip.Finite(minimumTargetDistance) || minimumTargetDistance < .1f)
            { reason = "观察站位调整需要有限目标与 0.2–1.2 米的明确距离。"; return false; }
            Vector3 toward = target - start; toward.y = 0;
            float range = toward.magnitude;
            if (range < .15f || range > MaximumTargetDistance)
            { reason = "目标的水平距离不在本次局部观察调整范围内，无法确定可靠方向。"; return false; }
            toward /= range;
            Vector3 offset;
            switch (direction)
            {
                case "farther": offset = -toward; break;
                case "closer": offset = toward; break;
                case "left": offset = -Vector3.Cross(Vector3.up, toward); break;
                case "right": offset = Vector3.Cross(Vector3.up, toward); break;
                default: reason = "观察站位方向必须是 farther、closer、left 或 right。"; return false;
            }
            ground = start + offset * distance;
            Vector3 remaining = target - ground; remaining.y = 0;
            if (direction == "closer" && range - distance < minimumTargetDistance || remaining.magnitude < minimumTargetDistance)
            { reason = "请求会越过目标或进入目标点的最小身体间距，未改成其它方向。"; return false; }
            return true;
        }

        public static bool RouteMatchesOffset(ArdyRoomRoute route, Vector3 start, Vector3 target, out string reason)
        {
            reason = null;
            Vector3 axis = target - start; axis.y = 0;
            float length = axis.magnitude;
            if (route?.Corners == null || route.Corners.Length == 0 || length < .1f ||
                route.Length > length + .15f)
            { reason = "该方向没有对应的短距离直移路线；未选择绕行或其它站位。"; return false; }
            Vector3 endpointOffset = route.Corners[route.Corners.Length - 1] - target; endpointOffset.y = 0;
            if (endpointOffset.magnitude > .025f)
            { reason = "导航终点偏离请求的水平站位，未改选另一目标。"; return false; }
            axis /= length; float previous = -.001f;
            foreach (Vector3 corner in route.Corners)
            {
                Vector3 delta = corner - start; delta.y = 0;
                float progress = Vector3.Dot(delta, axis);
                if (progress < previous - .025f || progress < -.025f || progress > length + .025f ||
                    (delta - axis * Mathf.Clamp(progress, 0, length)).magnitude > CorridorTolerance)
                { reason = "导航路线偏离请求方向或需要回绕；未暗中改变观察动作。"; return false; }
                previous = progress;
            }
            return true;
        }

        public static bool TryPlan(ArdyRoomNavigation navigation, Vector3 avatarGround,
            Vector3 surfacePoint, Transform userAnchor, string direction, float distance,
            out ArdyRoomObjectApproachPlan result, out string reason, out ArdyRoomObjectApproachObservation observation)
        {
            result = null;
            observation = new ArdyRoomObjectApproachObservation { visibleSurfacePoint = surfacePoint,
                scope = "Exactly one requested same-floor offset relative to the confirmed target. No alternate direction or farther candidate was selected. Reaching the station does not prove a better or complete image." };
            var evidence = observation;
            bool Fail(string code, string message) { evidence.code = code; evidence.reason = message; return false; }
            reason = null;
            if (navigation == null || !navigation.isActiveAndEnabled || !navigation.IsBuilt)
            { reason = "房间导航尚未准备好。"; return Fail("navigation-unavailable", reason); }
            observation.stage = "start";
            if (!navigation.TryValidatePlanStart(avatarGround, out Vector3 start, out reason, out var startEvidence))
            { observation.preflightFailure = startEvidence; return Fail(startEvidence.code, reason); }
            if (!TryOffset(start, surfacePoint, direction, distance, Mathf.Max(.45f, navigation.AgentRadius + .15f), out Vector3 target, out reason))
                return Fail("invalid-view-offset", reason);
            observation.userClearance = new ArdyRoomUserClearanceObservation { trackedHead = userAnchor != null ? userAnchor.position : Vector3.zero };
            if (!ArdyRoomObjectApproachPlanner.TryGetUserClearanceCenter(userAnchor, start.y, out Vector3 user, out reason))
            { observation.userClearance.reason = reason; return Fail("user-clearance-anchor-unavailable", reason); }
            observation.userClearance.valid = true; observation.userClearance.center = user;
            observation.stage = "requested-station"; observation.candidateCount = 1;
            var candidate = new ArdyRoomApproachCandidateObservation { index = 0, candidate = target };
            observation.candidates.Add(candidate);
            if (!navigation.TryPlan(start, target, out var route, out reason, out var detail))
            { candidate.navigation = detail; observation.navigationRejected = 1; return Fail(detail.code, reason); }
            candidate.navigation = detail; candidate.routeLength = route.Length;
            if (!RouteMatchesOffset(route, start, target, out reason))
            { observation.navigationRejected = 1; return Fail("requested-offset-route-deviation", reason); }
            float radius = navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
            if (!ArdyRoomApproachPlanner.RouteKeepsUserClearance(route, user, radius))
            { reason = "请求方向会穿过用户身体周围的保留空间；未改选另一方向。"; observation.userClearanceRejected = 1; return Fail("user-clearance", reason); }
            // Navigation has separately certified actual support height and its
            // permitted same-floor variation (for example a thin carpet).
            // Only XZ is requested by this relative operation; do not introduce
            // a second tighter height rule or retain an unprojected ground Y.
            target = route.Corners[route.Corners.Length - 1];
            candidate.accepted = true; observation.acceptedCount = 1; observation.selectedCandidate = 0;
            observation.success = true; observation.stage = "completed"; observation.plannedGround = target;
            observation.reason = "明确方向的同层观察站位通过审核，尚未移动或确认视野改善。";
            result = new ArdyRoomObjectApproachPlan { GroundTarget = target, FacePoint = surfacePoint,
                UserClearanceCenter = user, UserClearanceRadius = radius, Route = route, Observation = observation };
            reason = null; return true;
        }
    }
}
