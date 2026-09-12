using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyRoomUserSupportObservation
    {
        public bool found, verified;
        public string objectName = "", objectPath = "", colliderType = "", reason = "";
        public int colliderId;
        public Vector3 head, firstHit, ground;
        public float headHeight = -1;
    }

    [Serializable]
    public sealed class ArdyRoomApproachCandidateObservation
    {
        public int index, ring, turn;
        public Vector3 candidate;
        public bool accepted;
        public float routeLength = -1;
        public ArdyRoomRouteDiagnostic navigation;
    }

    /// <summary>The finite checks attempted for this snapshot, never an exhaustive reachability claim.</summary>
    [Serializable]
    public sealed class ArdyRoomApproachObservation
    {
        public string stage = "setup", code = "", reason = "", summary = "";
        public bool success;
        public int candidateCount, acceptedCount, startRejected, targetRejected, pathRejected,
            userClearanceRejected, otherRejected, selectedCandidate = -1;
        public ArdyRoomUserSupportObservation userSupport;
        public ArdyRoomRouteDiagnostic preflightFailure, stationaryAttempt;
        public List<ArdyRoomApproachCandidateObservation> candidates = new List<ArdyRoomApproachCandidateObservation>(24);
    }

    /// <summary>A same-floor approach to one explicitly selected user head anchor, sampled once.</summary>
    public sealed class ArdyRoomApproachPlan
    {
        public Vector3 GroundTarget { get; internal set; }
        public Vector3 FacePoint { get; internal set; }
        public Vector3 UserGround { get; internal set; }
        public ArdyRoomRoute Route { get; internal set; }
        public bool AlreadyInRange { get; internal set; }
        public float DesiredDistance { get; internal set; }
        public float UserClearanceRadius { get; internal set; }
        public ArdyRoomApproachObservation Observation { get; internal set; }
    }

    /// <summary>
    /// Bounded geometric planning; never substitutes a camera, avatar forward vector, or an inferred
    /// user position for the explicit head anchor. Execution must recheck a moving user's clearance.
    /// </summary>
    public static class ArdyRoomApproachPlanner
    {
        public const float UserBodyRadius = .25f;
        public const float UserClearance = .10f;
        public const float MinimumHeadHeight = .30f;
        public const float MaximumHeadHeight = 2.40f;
        private const float AlreadyNearTolerance = .15f;

        /// <summary>Recheck the explicit head anchor's current real support without planning any route.</summary>
        public static bool TryGetUserGround(ArdyRoomNavigation navigation, Transform userAnchor,
            out Vector3 ground, out string reason)
            => TryGetUserGround(navigation, userAnchor, out ground, out reason, out _);

        public static bool TryGetUserGround(ArdyRoomNavigation navigation, Transform userAnchor,
            out Vector3 ground, out string reason, out ArdyRoomUserSupportObservation observation)
        {
            ground = default;
            observation = new ArdyRoomUserSupportObservation();
            if (navigation == null || !navigation.isActiveAndEnabled || !navigation.IsBuilt)
                return Fail("房间导航尚未准备好。", out reason);
            if (userAnchor == null || !userAnchor.gameObject.activeInHierarchy ||
                !userAnchor.gameObject.scene.IsValid() || !userAnchor.gameObject.scene.isLoaded || !Finite(userAnchor.position))
                return Fail("需要明确且已启用的场景用户头部锚点。", out reason);
            return TryUserGround(navigation, userAnchor.position, userAnchor, out ground, out reason, out observation);
        }

        public static bool TryPlan(ArdyRoomNavigation navigation, Vector3 avatarGround, Transform userAnchor,
            float desiredDistance, out ArdyRoomApproachPlan result, out string reason)
            => TryPlan(navigation, avatarGround, userAnchor, desiredDistance, out result, out reason, out _);

        public static bool TryPlan(ArdyRoomNavigation navigation, Vector3 avatarGround, Transform userAnchor,
            float desiredDistance, out ArdyRoomApproachPlan result, out string reason, out ArdyRoomApproachObservation observation)
        {
            result = null;
            observation = new ArdyRoomApproachObservation();
            if (navigation == null || !navigation.isActiveAndEnabled || !navigation.IsBuilt)
                return PlanFail(observation, "navigation-unavailable", "房间导航尚未准备好。", out reason);
            if (userAnchor == null || !userAnchor.gameObject.activeInHierarchy ||
                !userAnchor.gameObject.scene.IsValid() || !userAnchor.gameObject.scene.isLoaded)
                return PlanFail(observation, "user-anchor-invalid", "需要明确且已启用的场景用户头部锚点。", out reason);
            if (!Finite(avatarGround) || !Finite(userAnchor.position) || !Finite(desiredDistance) ||
                desiredDistance < .5f || desiredDistance > 3f)
                return PlanFail(observation, "input-invalid", "角色坐标、用户坐标或接近距离无效（距离须为 0.5–3 米）。", out reason);
            observation.stage = "start";
            if (!navigation.TryProjectGround(avatarGround, out var start, out reason, out var startObservation))
            {
                startObservation.stage = "start";
                observation.preflightFailure = startObservation;
                return PlanFail(observation, startObservation.code, reason, out reason);
            }
            Vector3 face = userAnchor.position;
            observation.stage = "user-support";
            if (!TryUserGround(navigation, face, userAnchor, out var userGround, out reason, out var support))
            {
                observation.userSupport = support;
                return PlanFail(observation, "user-support-invalid", reason, out reason);
            }
            observation.userSupport = support;
            observation.stage = "same-floor";
            if (Mathf.Abs(start.y - userGround.y) > navigation.MaxRouteHeightVariation)
                return PlanFail(observation, "different-plane", $"用户与角色不在已验证的同层地面：角色地面 Y={start.y:F3} 米，用户地面 Y={userGround.y:F3} 米，高差 {Mathf.Abs(start.y-userGround.y):F3} 米，上限 {navigation.MaxRouteHeightVariation:F3} 米。本阶段不能跨台阶或楼层走近。", out reason);

            float radius = navigation.AgentRadius + UserBodyRadius + UserClearance;
            float distance = Mathf.Max(desiredDistance, radius + .10f);
            Vector3 radial = Horizontal(start - userGround);
            if (Mathf.Abs(radial.magnitude - distance) <= AlreadyNearTolerance && radial.magnitude >= radius + .05f)
            {
                bool stationaryValid = navigation.TryPlan(start, start, out var stationary, out _, out var stationaryObservation);
                observation.stationaryAttempt = stationaryObservation;
                if (stationaryValid)
                {
                    observation.success = true;
                    observation.stage = "completed";
                    observation.summary = "当前位置已通过站位检查；仅需检查朝向。";
                    result = Make(start, face, userGround, stationary, true, distance, radius, observation);
                    reason = null;
                    return true;
                }
            }

            // The cardinal tie-break only orders candidates when positions coincide; it never
            // invents the user's location or a destination without a real ground/path check.
            Vector3 preferred = radial.sqrMagnitude > .000001f ? radial.normalized : Vector3.forward;
            ArdyRoomRoute best = null;
            Vector3 bestGround = default;
            float bestScore = float.PositiveInfinity;
            observation.stage = "candidates";
            // At most 24 ordinary NavMesh plans. Alternate +/- directions to avoid one-sided bias.
            int[] turns = { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6 };
            for (int ring = 0; ring < 2; ring++)
            {
                float ringDistance = distance + ring * .20f;
                foreach (int turn in turns)
                {
                    Vector3 direction = Quaternion.AngleAxis(turn * 30f, Vector3.up) * preferred;
                    Vector3 candidate = userGround + direction * ringDistance;
                    var candidateObservation = new ArdyRoomApproachCandidateObservation {
                        index = observation.candidateCount++, ring = ring, turn = turn, candidate = candidate };
                    observation.candidates.Add(candidateObservation);
                    bool navigable = navigation.TryPlan(start, candidate, out var route, out _, out var routeObservation);
                    candidateObservation.navigation = routeObservation;
                    if (!navigable)
                    {
                        switch (routeObservation.stage)
                        {
                            case "start": observation.startRejected++; break;
                            case "target": observation.targetRejected++; break;
                            case "path": observation.pathRejected++; break;
                            default: observation.otherRejected++; break;
                        }
                        continue;
                    }
                    candidateObservation.routeLength = route.Length;
                    if (!RouteKeepsUserClearance(route, userGround, radius))
                    {
                        observation.userClearanceRejected++;
                        candidateObservation.navigation = new ArdyRoomRouteDiagnostic {
                            stage = "user-clearance", code = "user-clearance", start = start, target = candidate,
                            reason = "路线会穿过用户身体周围的保留空间。" };
                        continue;
                    }
                    candidateObservation.accepted = true;
                    observation.acceptedCount++;
                    float score = route.Length + Mathf.Abs(turn) * .015f + ring * .15f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = route;
                    bestGround = route.Corners[route.Corners.Length - 1];
                    observation.selectedCandidate = candidateObservation.index;
                }
            }
            if (best == null)
                return PlanFail(observation, "bounded-candidates-rejected",
                    "用户附近没有本阶段可达且留有身体距离的站位。" + CandidateSummary(observation), out reason);
            observation.success = true;
            observation.stage = "completed";
            observation.summary = CandidateSummary(observation);
            result = Make(bestGround, face, userGround, best, false, distance, radius, observation);
            reason = null;
            return true;
        }

        /// <summary>
        /// Allows an already overlapping start to retreat monotonically; otherwise no segment may
        /// enter the user's reserved horizontal disk. Applies to snapshot plans and live root steps.
        /// </summary>
        public static bool StepKeepsUserClearance(Vector3 from, Vector3 to, Vector3 userGround, float radius)
        {
            if (!Finite(from) || !Finite(to) || !Finite(userGround) || !Finite(radius) || radius <= 0f) return false;
            Vector3 offset = Horizontal(from - userGround), delta = Horizontal(to - from);
            float radiusSquared = radius * radius;
            if (offset.sqrMagnitude < radiusSquared)
                return Vector3.Dot(offset, delta) >= -0.000001f &&
                    (offset + delta).sqrMagnitude >= offset.sqrMagnitude - 0.000001f;
            float t = delta.sqrMagnitude < .00000001f ? 0f :
                Mathf.Clamp01(-Vector3.Dot(offset, delta) / delta.sqrMagnitude);
            return (offset + delta * t).sqrMagnitude >= radiusSquared - .000001f;
        }

        public static bool RouteKeepsUserClearance(ArdyRoomRoute route, Vector3 userGround, float radius)
        {
            if (route == null || route.Corners == null || route.Corners.Length == 0 ||
                !Finite(userGround) || !Finite(radius) || radius <= 0f) return false;
            foreach (var corner in route.Corners) if (!Finite(corner)) return false;
            if (Horizontal(route.Corners[route.Corners.Length - 1] - userGround).magnitude < radius) return false;
            for (int i = 1; i < route.Corners.Length; i++)
                if (!StepKeepsUserClearance(route.Corners[i - 1], route.Corners[i], userGround, radius)) return false;
            return true;
        }

        private static bool TryUserGround(ArdyRoomNavigation navigation, Vector3 head, Transform userAnchor,
            out Vector3 ground, out string reason)
            => TryUserGround(navigation, head, userAnchor, out ground, out reason, out _);

        private static bool TryUserGround(ArdyRoomNavigation navigation, Vector3 head, Transform userAnchor,
            out Vector3 ground, out string reason, out ArdyRoomUserSupportObservation observation)
        {
            ground = default;
            observation = new ArdyRoomUserSupportObservation { head = head };
            Physics.SyncTransforms();
            bool found = false;
            RaycastHit nearest = default;
            // Find the first actual support beneath the head, rather than sampling NavMesh at head
            // height or searching only at the avatar's floor (which could hide another level).
            foreach (var hit in Physics.RaycastAll(head + Vector3.up * .01f, Vector3.down,
                MaximumHeadHeight + .02f, ~0, QueryTriggerInteraction.Ignore))
            {
                var collider = hit.collider;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                // A collider on the explicitly selected user's own head/body is not room support.
                if (collider.transform.IsChildOf(userAnchor) || userAnchor.IsChildOf(collider.transform)) continue;
                if (!found || hit.distance < nearest.distance) { nearest = hit; found = true; }
            }
            if (!found) return SupportFail(observation, $"用户视点下方 {MaximumHeadHeight:F2} 米内没有可确认的真实房间支撑面；请检查相机位置，不能据此断定具体楼层。", out reason);
            float headHeight = head.y - nearest.point.y;
            observation.found = true;
            observation.firstHit = nearest.point;
            observation.objectName = nearest.collider.name;
            observation.colliderId = nearest.collider.GetInstanceID();
            observation.colliderType = nearest.collider.GetType().Name;
            observation.objectPath = nearest.collider.name;
            for (Transform parent = nearest.collider.transform.parent; parent != null; parent = parent.parent)
                observation.objectPath = parent.name + "/" + observation.objectPath;
            observation.headHeight = headHeight;
            if (headHeight < MinimumHeadHeight || headHeight > MaximumHeadHeight)
                return SupportFail(observation, $"用户视点距下方首个实体 {nearest.collider.name} 为 {headHeight:F3} 米，允许范围 {MinimumHeadHeight:F1}–{MaximumHeadHeight:F1} 米。视点 Y={head.y:F3}，命中 Y={nearest.point.y:F3}；请检查是否飞得过低、过高或位于家具上方。", out reason);
            // Navigation is the authority on registered support, including its verified thin-cover
            // proxies outside the source hierarchy. An unregistered higher surface cannot be skipped.
            if (!navigation.TryProjectGround(nearest.point, out ground, out reason))
                return SupportFail(observation, $"用户下方首个实体 {nearest.collider.name}（Y={nearest.point.y:F3}）不属于可验证的已登记房间地面。 " + reason, out reason);
            observation.ground = ground;
            if (Mathf.Abs(ground.y - nearest.point.y) > .005f)
                return SupportFail(observation, $"用户下方首个支撑面 {nearest.collider.name}（Y={nearest.point.y:F3}）与已登记地面（Y={ground.y:F3}）不一致；拒绝穿过支撑面投影。", out reason);
            observation.verified = true;
            reason = null;
            return true;
        }

        private static bool SupportFail(ArdyRoomUserSupportObservation observation, string text, out string reason)
        { observation.reason = text; return Fail(text, out reason); }

        // A change of eye height alone does not change the horizontal destination. The caller
        // still validates head height, tracking, floor support and body clearance separately.
        public static float UserGroundDisplacement(Vector3 sampledHead, Vector3 currentHead)
            => Horizontal(currentHead - sampledHead).magnitude;

        private static ArdyRoomApproachPlan Make(Vector3 ground, Vector3 face, Vector3 userGround,
            ArdyRoomRoute route, bool near, float distance, float radius, ArdyRoomApproachObservation observation) => new ArdyRoomApproachPlan {
                GroundTarget = ground, FacePoint = face, UserGround = userGround, Route = route,
                AlreadyInRange = near, DesiredDistance = distance, UserClearanceRadius = radius, Observation = observation };
        private static string CandidateSummary(ArdyRoomApproachObservation value)
            => $"本次仅检查 {value.candidateCount} 个候选站位：可用 {value.acceptedCount}，起点检查拒绝 {value.startRejected}，" +
                $"终点检查拒绝 {value.targetRejected}，途中检查拒绝 {value.pathRejected}，用户身体间距拒绝 {value.userClearanceRejected}，其他拒绝 {value.otherRejected}。" +
                "这是本次有限搜索结果，不能据此断定所有路线都被某件家具挡住。";
        private static bool PlanFail(ArdyRoomApproachObservation observation, string code, string text, out string reason)
        { observation.code = code; observation.reason = observation.summary = text; return Fail(text, out reason); }
        private static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Fail(string text, out string reason) { reason = text; return false; }
    }
}
