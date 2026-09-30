using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Walks to the measured visible floor itself, never a floor projected below furniture.</summary>
    public static class ArdyRoomGroundMovementPlanner
    {
        public const float MaximumSurfaceSupportError = .025f;

        public static bool TryPlan(ArdyRoomNavigation navigation, Vector3 avatarGround,
            Vector3 visibleFloorPoint, Transform userAnchor, out ArdyRoomObjectApproachPlan result,
            out string reason, out ArdyRoomObjectApproachObservation observation)
        {
            result = null;
            observation = new ArdyRoomObjectApproachObservation {
                visibleSurfacePoint = visibleFloorPoint,
                scope = "One measured visible floor destination; no object approach offset or hidden/random destination. Complete same-plane path and body clearance are checked before native ARDY execution." };
            if (navigation == null || !navigation.isActiveAndEnabled || !navigation.IsBuilt)
            { reason = "房间导航尚未准备好。"; return Fail(observation, "navigation-unavailable", reason); }
            if (!ArdyLocomotionClip.Finite(avatarGround) || !ArdyLocomotionClip.Finite(visibleFloorPoint))
            { reason = "可见地面坐标无效。"; return Fail(observation, "invalid-input", reason); }
            observation.stage = "visible-floor";
            if (!navigation.TryValidatePlanStart(avatarGround, out Vector3 start, out reason, out var check))
            { observation.preflightFailure = check; return Fail(observation, check.code, reason); }
            // Test the measured surface at its own height first. Do not replace its Y with the
            // actor's floor height: doing so would silently turn a tabletop into unseen floor.
            if (Mathf.Abs(visibleFloorPoint.y - start.y) > navigation.MaxRouteHeightVariation + .001f)
            { reason = "选中的可见表面不在角色当前同层地面，不能把家具或其他楼层当作空地。"; return Fail(observation, "visible-surface-not-current-floor", reason); }
            if (!navigation.TryProjectGround(visibleFloorPoint, out Vector3 target, out reason, out check))
            { observation.preflightFailure = check; return Fail(observation, check.code, reason); }
            if (Vector3.Distance(target, visibleFloorPoint) > MaximumSurfaceSupportError)
            { reason = "图片中的表面与实际支撑地板不一致，需要重新选择明确可见的地板内部。"; return Fail(observation, "visible-surface-support-mismatch", reason); }
            observation.stage = "path";
            if (!navigation.TryPlan(start, target, out var route, out reason, out check))
            { observation.preflightFailure = check; return Fail(observation, check.code, reason); }
            observation.stage = "user-clearance";
            if (!ArdyRoomObjectApproachPlanner.TryGetUserClearanceCenter(userAnchor, start.y, out Vector3 userCenter, out reason))
                return Fail(observation, "user-clearance-anchor-unavailable", reason);
            float radius = navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
            if (!ArdyRoomApproachPlanner.RouteKeepsUserClearance(route, userCenter, radius))
            { reason = "到可见空地的路线经过用户身体净距范围。"; return Fail(observation, "user-clearance", reason); }
            Vector3 direction = route.Corners.Length > 1 ? target - route.Corners[route.Corners.Length - 2] : Vector3.forward;
            direction.y = 0;
            if (direction.sqrMagnitude < .0001f) direction = Vector3.forward;
            observation.success = true; observation.stage = "completed"; observation.plannedGround = target;
            observation.candidateCount = observation.acceptedCount = 1; observation.selectedCandidate = 0;
            observation.userClearance = new ArdyRoomUserClearanceObservation {
                valid = true, trackedHead = userAnchor.position, center = userCenter };
            result = new ArdyRoomObjectApproachPlan { GroundTarget = target, FacePoint = target + direction.normalized,
                UserClearanceCenter = userCenter, UserClearanceRadius = radius, Route = route, Observation = observation };
            reason = null;
            return true;
        }

        private static bool Fail(ArdyRoomObjectApproachObservation observation, string code, string reason)
        { observation.code = code; observation.reason = reason; return false; }
    }
}
