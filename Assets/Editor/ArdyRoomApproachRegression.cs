using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Real Physics/NavMesh fixtures for explicit user anchors. Never opens the user's room.</summary>
public static class ArdyRoomApproachRegression
{
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, plannerSha256, navigationSha256, harnessSha256;
        public string scope = "Synthetic real Unity Physics/NavMesh rooms; explicit head-to-floor projection, same-floor approach stand-off, reachable candidates and route/user clearance. No real avatar, ARDY, voice, tracking quality or visual quality assertion.";
        public int checks;
        public List<string> fixtures = new List<string>();
    }
    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Root, Avatar, User;
        public readonly ArdyRoomNavigation Navigation;
        public Fixture(string name)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Root = new GameObject("ApproachFixture_" + name);
            SceneManager.MoveGameObjectToScene(Root, scene);
            Avatar = new GameObject("ExplicitAvatar");
            User = new GameObject("ExplicitUserHead");
            SceneManager.MoveGameObjectToScene(Avatar, scene);
            SceneManager.MoveGameObjectToScene(User, scene);
            User.transform.position = new Vector3(0f, 1.6f, 0f);
            Navigation = Root.AddComponent<ArdyRoomNavigation>();
        }
        public BoxCollider Box(string name, Vector3 center, Vector3 size)
        {
            var item = new GameObject(name);
            item.transform.SetParent(Root.transform, false);
            item.transform.position = center;
            var box = item.AddComponent<BoxCollider>(); box.size = size;
            return box;
        }
        public void Floor(float size = 12f) => Box("Floor", new Vector3(0f, -.1f, 0f), new Vector3(size, .2f, size));
        public void Build() => Check(Navigation.Build(Root.transform, Avatar.transform, out var reason), "Build: " + reason);
        public bool Plan(Vector3 start, out ArdyRoomApproachPlan plan, out string reason)
            => ArdyRoomApproachPlanner.TryPlan(Navigation, start, User.transform, 1f, out plan, out reason);
        public void Dispose()
        {
            Navigation.Clear();
            Object.DestroyImmediate(Root); Object.DestroyImmediate(Avatar); Object.DestroyImmediate(User);
        }
    }
    private static int checks;

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use only the isolated unity-naturalness-validation project in batch mode.");
        string output = Path.GetFullPath(Argument("-ardyRoomApproachOutput") ?? Path.Combine(Application.dataPath, "../Logs/ardy-room-approach-regression"));
        Directory.CreateDirectory(output);
        checks = 0;
        var report = new Report { unityVersion = Application.unityVersion,
            plannerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomApproachPlanner.cs"),
            navigationSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs"),
            harnessSha256 = Hash("Assets/Editor/ArdyRoomApproachRegression.cs") };
        int before = NavMesh.GetSettingsCount();
        try
        {
            ExerciseClearanceMath(report);
            ExerciseFlat(report);
            ExerciseFurniture(report);
            ExerciseVerifiedCover(report);
            ExerciseMissingAndOtherFloors(report);
            ExerciseUnreachable(report);
            ExerciseRejectionDiagnostics(report);
            Check(NavMesh.GetSettingsCount() == before, "Every owned NavMesh setting must be released.");
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks;
        File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[ArdyRoomApproachRegression] " + report.status + " " + checks + " checks: " + output);
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }

    private static void ExerciseClearanceMath(Report report)
    {
        Check(!ArdyRoomApproachPlanner.StepKeepsUserClearance(new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f), Vector3.zero, .65f), "Long segment must not cross the user's disk even when both endpoints are safe.");
        Check(ArdyRoomApproachPlanner.StepKeepsUserClearance(new Vector3(.3f, 0f, 0f), Vector3.right, Vector3.zero, .65f), "Already-close start may retreat directly outward.");
        Check(!ArdyRoomApproachPlanner.StepKeepsUserClearance(new Vector3(.3f, 0f, 0f), Vector3.left, Vector3.zero, .65f), "Already-close start must not pass through user to the other side.");
        Check(!ArdyRoomApproachPlanner.StepKeepsUserClearance(new Vector3(1f, 0f, 0f), new Vector3(.6f, 0f, 0f), Vector3.zero, .65f), "A safe start must not end inside the user disk.");
        Check(ArdyRoomApproachPlanner.StepKeepsUserClearance(new Vector3(-1f, 0f, .8f), new Vector3(1f, 0f, .8f), Vector3.zero, .65f), "A tangential safe route should remain allowed.");
        Check(!ArdyRoomApproachPlanner.StepKeepsUserClearance(Vector3.zero, Vector3.one, Vector3.zero, float.NaN), "Invalid radius cannot bypass live clearance.");
        Check(ArdyRoomApproachPlanner.UserGroundDisplacement(new Vector3(1, 1.6f, 2), new Vector3(1, .9f, 2)) == 0,
            "A valid eye-height change must not be confused with a moving ground target.");
        Check(Mathf.Abs(ArdyRoomApproachPlanner.UserGroundDisplacement(new Vector3(1, 1.6f, 2), new Vector3(1.5f, .9f, 2)) - .5f) < .0001f,
            "Horizontal displacement must still invalidate the sampled destination.");
        report.fixtures.Add("user-disk crossings and already-close retreat");
    }

    private static void ExerciseFlat(Report report)
    {
        using (var f = new Fixture("flat"))
        {
            f.Floor(); f.Build();
            var start = new Vector3(-3f, 0f, 0f);
            Check(f.Plan(start, out var plan, out var reason), "Approach on flat floor: " + reason);
            Check(!plan.AlreadyInRange, "Three metres away must produce movement.");
            Check(Mathf.Abs(plan.UserGround.y) < .005f && Mathf.Abs(plan.GroundTarget.y) < .005f, "Head height must never become target floor height.");
            Check(Vector3.Distance(plan.FacePoint, f.User.transform.position) < .00001f, "Facing target is the explicit anchor snapshot.");
            Check(plan.GroundTarget.x < -.85f && Mathf.Abs(plan.GroundTarget.z) < .1f, "Open-room approach should retain the current user-relative side.");
            ValidatePlan(plan);
            Vector3 target = plan.GroundTarget;
            f.User.transform.position += new Vector3(2f, 0f, 0f);
            Check(plan.GroundTarget == target && plan.FacePoint.x == 0f, "Existing plans must stay snapshots when anchor moves.");
            f.User.transform.position = new Vector3(0f, 1.6f, 0f);
            var already = new Vector3(-1.08f, 0f, 0f);
            Check(f.Plan(already, out plan, out reason) && plan.AlreadyInRange, "Already comfortably near needs only facing: " + reason);
            Check(Vector3.Distance(plan.GroundTarget, already) < .001f, "Already-near target must not move the avatar.");
            Check(f.Plan(new Vector3(-.35f, 0f, 0f), out plan, out reason), "Too-close start should retreat: " + reason);
            Check(!plan.AlreadyInRange && plan.GroundTarget.x < -.85f, "Too-close approach retreats on the same side.");
            ValidatePlan(plan);
            Check(!ArdyRoomApproachPlanner.TryPlan(f.Navigation, start, null, 1f, out _, out reason) && !string.IsNullOrEmpty(reason), "Missing anchor must be rejected without camera fallback.");
            f.User.SetActive(false);
            Check(!f.Plan(start, out _, out _), "Disabled user cannot supply an implicit target.");
            f.User.SetActive(true);
            Check(!ArdyRoomApproachPlanner.TryPlan(f.Navigation, start, f.User.transform, float.NaN, out _, out _), "Invalid distance is rejected.");
            f.User.transform.position = new Vector3(0f, .05f, 0f);
            Check(!f.Plan(start, out _, out _), "A feet anchor is rejected when a head anchor is required.");
            Check(!ArdyRoomApproachPlanner.TryGetUserGround(f.Navigation, f.User.transform, out _, out var lowReason, out var lowObservation) &&
                lowObservation.found && !lowObservation.verified && lowObservation.objectName == "Floor" &&
                Mathf.Abs(lowObservation.headHeight - .05f) < .001f && lowObservation.colliderId != 0 &&
                lowObservation.objectPath.Contains("Floor") && lowReason.Contains("Floor"),
                "Rejected user height must retain the measured first support and explicit reason.");
            f.User.transform.position = new Vector3(0f, .8f, 0f);
            Check(f.Plan(start, out _, out reason), "Low seated head remains supported: " + reason);
            Check(ArdyRoomApproachPlanner.TryGetUserGround(f.Navigation, f.User.transform, out var currentGround, out reason) &&
                Mathf.Abs(currentGround.y) < .005f, "Live support check uses current explicit anchor without route planning: " + reason);
            f.Root.GetComponentInChildren<BoxCollider>().enabled = false;
            Check(!ArdyRoomApproachPlanner.TryGetUserGround(f.Navigation, f.User.transform, out _, out _), "Live support check must detect a removed floor even while the old NavMesh is still built.");
            report.fixtures.Add("flat approach, explicit head snapshot, already near, retreat, missing/disabled anchor, seated user");
        }
    }

    private static void ExerciseFurniture(Report report)
    {
        using (var f = new Fixture("furniture"))
        {
            f.Floor();
            f.Box("FurnitureAtPreferredStandOff", new Vector3(-1f, .65f, 0f), new Vector3(.7f, 1.3f, .8f));
            f.Build();
            Check(f.Plan(new Vector3(-3f, 0f, 0f), out var plan, out var reason), "Other bounded ring candidates should avoid furniture: " + reason);
            Check(Mathf.Abs(plan.GroundTarget.z) > .45f, "Furniture-blocked preferred station must not be used.");
            ValidatePlan(plan);
            report.fixtures.Add("bounded alternative station around furniture");
        }
    }

    private static void ExerciseMissingAndOtherFloors(Report report)
    {
        using (var f = new Fixture("user-without-floor"))
        {
            f.Box("AvatarFloor", new Vector3(-2.5f, -.1f, 0f), new Vector3(3f, .2f, 5f));
            f.Build();
            Check(!f.Plan(new Vector3(-3f, 0f, 0f), out var plan, out var reason) && plan == null && !string.IsNullOrEmpty(reason), "User over a void must be rejected.");
            report.fixtures.Add("user over unsupported void rejected");
        }
        using (var f = new Fixture("upper-floor"))
        {
            f.Floor();
            f.Box("UpperFloorBeneathUser", new Vector3(0f, 2.9f, 0f), new Vector3(4f, .2f, 4f));
            f.User.transform.position = new Vector3(0f, 4.6f, 0f);
            f.Build();
            Check(!f.Plan(new Vector3(-4f, 0f, 0f), out _, out var reason) && reason.Contains("同层"), "User on another floor must not snap down to avatar's floor: " + reason);
            report.fixtures.Add("first real support detects upstairs user and rejects cross-floor approach");
        }
        using (var f = new Fixture("raised-support"))
        {
            f.Floor();
            f.Box("RaisedPlatformUnderUser", new Vector3(0f, .35f, 0f), new Vector3(2f, .7f, 2f));
            f.User.transform.position = new Vector3(0f, 2.3f, 0f);
            f.Build();
            Check(!f.Plan(new Vector3(-3f, 0f, 0f), out _, out var reason) && reason.Contains("同层"), "A raised platform below head must not be skipped: " + reason);
            f.User.transform.position = new Vector3(3f, 4f, 0f);
            Check(!f.Plan(new Vector3(-3f, 0f, 0f), out _, out _), "Implausibly suspended head cannot select distant floor.");
            report.fixtures.Add("raised support and suspended head rejected");
        }
        using (var f = new Fixture("unregistered-support"))
        {
            f.Floor();
            var platform = new GameObject("ExternalUnregisteredPlatform");
            platform.transform.SetParent(f.Avatar.transform, false);
            platform.transform.position = new Vector3(0f, .35f, 0f);
            platform.AddComponent<BoxCollider>().size = new Vector3(2f, .7f, 2f);
            f.User.transform.position = new Vector3(0f, 2.3f, 0f);
            f.Build();
            Check(!f.Plan(new Vector3(-3f, 0f, 0f), out _, out var reason) && reason.Contains("已登记"), "Do not skip an unregistered solid platform to project user onto floor below: " + reason);
            report.fixtures.Add("first unregistered solid support cannot be skipped");
        }
    }

    private static void ExerciseVerifiedCover(Report report)
    {
        using (var f = new Fixture("verified-thin-cover"))
        {
            f.Floor();
            var cover = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cover.name = "VisualThinCoverOverOriginalFloor";
            cover.transform.SetParent(f.Root.transform, false);
            cover.transform.position = new Vector3(0f, .013f, 0f);
            cover.transform.localScale = new Vector3(4f, .026f, 4f);
            Object.DestroyImmediate(cover.GetComponent<Collider>());
            f.User.transform.position = new Vector3(0f, 1.626f, 0f);
            f.Build();
            Check(f.Navigation.ThinCoverCount == 1, "Fixture must actually use a verified thin-cover support proxy.");
            Check(f.Plan(new Vector3(-3f, 0f, 0f), out var plan, out var reason), "Navigation's verified cover proxy remains valid head support: " + reason);
            Check(Mathf.Abs(plan.UserGround.y - .026f) < .001f, "Head projection retains actual carpet surface height.");
            ValidatePlan(plan);
            report.fixtures.Add("verified thin-cover proxy support over original floor");
        }
    }

    private static void ExerciseUnreachable(Report report)
    {
        using (var f = new Fixture("sealed-wall"))
        {
            f.Floor(10f);
            f.Box("SealedWall", new Vector3(0f, 1f, 0f), new Vector3(.2f, 2f, 11f));
            f.User.transform.position = new Vector3(2f, 1.6f, 0f);
            f.Build();
            Check(!f.Plan(new Vector3(-3f, 0f, 0f), out var plan, out var reason) && plan == null && !string.IsNullOrEmpty(reason), "Unreachable user must not return a partial or opposite-side target.");
            report.fixtures.Add("all nearby user stations behind sealed wall rejected");
        }
    }

    private static void ExerciseRejectionDiagnostics(Report report)
    {
        using (var f = new Fixture("diagnostic-start-target-path"))
        {
            f.Floor(); f.Build();
            var start = new Vector3(-3f, 0f, 0f);
            var target = new Vector3(3f, 0f, 0f);
            // Added after baking: the real Physics guard must still catch the obstacle.
            var obstacle = f.Box("DynamicBodyObstacle", start + Vector3.up * .85f, new Vector3(.6f, 1.7f, .6f));
            Check(!f.Navigation.TryPlan(start, target, out _, out var reason, out var observation) &&
                observation.stage == "start" && observation.code == "body-overlap", "Start overlap classification: " + reason);
            Check(observation.hasSample && Vector3.Distance(observation.sample, start) < .001f &&
                observation.colliderId == obstacle.GetInstanceID() && observation.colliderPath == f.Root.name + "/DynamicBodyObstacle" &&
                observation.sourceObjectPath == observation.colliderPath && observation.colliderType == "BoxCollider",
                "Start rejection retains tested position and the actual blocking collider hierarchy.");
            Check(!ArdyRoomApproachPlanner.TryPlan(f.Navigation, start, f.User.transform, 1f, out _, out _, out var approach) &&
                approach.candidateCount == 24 && approach.startRejected == 24 && approach.targetRejected == 0 && approach.pathRejected == 0,
                "Start obstruction must not be attributed to the 24 different candidate destinations.");
            obstacle.transform.position = target + Vector3.up * .85f;
            Check(!f.Navigation.TryPlan(start, target, out _, out reason, out observation) &&
                observation.stage == "target" && observation.code == "body-overlap" &&
                Vector3.Distance(observation.sample, target) < .001f && observation.colliderId == obstacle.GetInstanceID(),
                "Target overlap is separately identified: " + reason);
            obstacle.transform.position = Vector3.up * .85f;
            Check(!f.Navigation.TryPlan(start, target, out _, out reason, out observation) && observation.stage == "path" &&
                (observation.code == "body-overlap" || observation.code == "body-sweep-blocked"),
                "An obstacle crossed between safe endpoints is a path rejection: " + reason);
            Check(observation.segmentIndex >= 0 && observation.hasSample && Mathf.Abs(observation.sample.x) < 1f &&
                observation.colliderId == obstacle.GetInstanceID() && Vector3.Distance(observation.from, observation.to) > .5f,
                "Intermediate rejection retains the checked route segment and actual obstacle, not the destination.");
            report.fixtures.Add("dynamic start, target and intermediate capsule obstruction retain separate stages and collider paths");
        }
        using (var f = new Fixture("diagnostic-navmesh-boundary"))
        {
            f.Floor();
            // Deliberately expose the gap between the unchanged physical radius and a conservative bake.
            f.Navigation.PlanningClearance = .15f;
            f.Build();
            // Locate the first rejected point in a fixed 20 cm boundary band. Unity's bake voxel
            // alignment can shift the edge; every tested physical footprint stays strictly on Floor.
            Vector3 edge = default;
            string reason = null;
            ArdyRoomRouteDiagnostic observation = null;
            bool boundaryFound = false;
            for (int i = 0; i < 40; i++)
            {
                edge = new Vector3(5.50f + .005f * i, 0f, 0f);
                if (f.Navigation.TryPlan(Vector3.zero, edge, out _, out reason, out observation)) continue;
                boundaryFound = observation.stage == "target" && observation.code == "navmesh-boundary";
                break;
            }
            Check(boundaryFound && observation.colliderId == 0,
                "A conservative target NavMesh boundary must not invent a collider blockage: " + reason);
            Check(!f.Navigation.TryPlan(edge, Vector3.zero, out _, out reason, out observation) &&
                observation.stage == "start" && observation.code == "navmesh-boundary",
                "The same boundary at the start is distinguished from a blocked destination: " + reason);
            report.fixtures.Add("safe physical footprint outside conservative NavMesh classified by endpoint without fabricated collider");
        }
        using (var f = new Fixture("diagnostic-missing-path-support"))
        {
            f.Box("LeftFloor", new Vector3(-3.5f, -.1f, 0f), new Vector3(5f, .2f, 8f));
            var middle = f.Box("RemovableMiddleFloor", new Vector3(0f, -.1f, 0f), new Vector3(2f, .2f, 8f));
            f.Box("RightFloor", new Vector3(3.5f, -.1f, 0f), new Vector3(5f, .2f, 8f));
            f.Build();
            middle.enabled = false;
            Check(!f.Navigation.TryPlan(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), out _, out var reason, out var observation) &&
                observation.stage == "path" && observation.code == "support-missing" && observation.hasSample && observation.colliderId == 0,
                "Stale NavMesh across a removed middle floor reports intermediate missing support without a fictional obstacle: " + reason);
            report.fixtures.Add("removed intermediate floor is a path support rejection even while the baked path remains complete");
        }
        using (var f = new Fixture("diagnostic-mixed-candidates"))
        {
            f.Floor(10f);
            f.Box("SealedWall", new Vector3(0f, 1f, 0f), new Vector3(.2f, 2f, 11f));
            f.User.transform.position = new Vector3(2f, 1.6f, 0f);
            f.Build();
            f.Box("LastCandidateSofa", new Vector3(3.2f, .85f, 0f), new Vector3(.2f, 1.7f, .35f));
            Check(!ArdyRoomApproachPlanner.TryPlan(f.Navigation, new Vector3(-3f, 0f, 0f), f.User.transform, 1f,
                out _, out var reason, out var observation) && observation.candidateCount == 24 && observation.candidates.Count == 24 &&
                observation.acceptedCount == 0 && observation.selectedCandidate == -1, "Finite candidate failure preserves every attempted station: " + reason);
            Check(observation.pathRejected > 0 && observation.targetRejected > 0 &&
                observation.startRejected + observation.targetRejected + observation.pathRejected + observation.userClearanceRejected + observation.otherRejected == 24,
                "Candidate rejection totals must cover mixed causes instead of retaining only the last sofa.");
            Check(!reason.Contains("LastCandidateSofa") && reason.Contains("24") && reason.Contains("有限搜索"),
                "Model summary must describe the bounded search and must not attribute all routes to its final collider.");
            foreach (var candidate in observation.candidates)
                Check(!candidate.accepted && candidate.navigation != null && !string.IsNullOrEmpty(candidate.navigation.code) &&
                    candidate.index >= 0 && candidate.index < 24, "Every rejected candidate keeps its own coded rejection.");
            var roundtrip = JsonUtility.FromJson<ArdyRoomApproachObservation>(JsonUtility.ToJson(observation));
            Check(roundtrip.candidates.Count == 24 && roundtrip.targetRejected == observation.targetRejected &&
                roundtrip.candidates[23].navigation.colliderPath.EndsWith("/LastCandidateSofa", StringComparison.Ordinal),
                "Serialized diagnostic must retain all 24 candidates and the final collider without promoting it to global cause.");
            report.fixtures.Add("24 bounded candidates retain mixed path/target rejections, counts and serialized per-candidate evidence");
        }
    }

    private static void ValidatePlan(ArdyRoomApproachPlan plan)
    {
        Check(plan.Route != null && plan.Route.Corners != null && plan.Route.Corners.Length > 0, "Accepted plan includes a complete checked route.");
        float minimum = float.PositiveInfinity;
        for (int i = 1; i < plan.Route.Corners.Length; i++)
        {
            Vector3 a = plan.Route.Corners[i - 1], b = plan.Route.Corners[i];
            for (int n = 0; n <= 30; n++)
            {
                Vector3 p = Vector3.Lerp(a, b, n / 30f) - plan.UserGround; p.y = 0f;
                minimum = Mathf.Min(minimum, p.magnitude);
            }
        }
        Vector3 begin = plan.Route.Corners[0] - plan.UserGround; begin.y = 0f;
        Check(minimum >= Mathf.Min(begin.magnitude, plan.UserClearanceRadius) - .001f, "Independently sampled route may never get closer than initial overlap or safe radius.");
        Vector3 end = plan.GroundTarget - plan.UserGround; end.y = 0f;
        Check(end.magnitude >= plan.UserClearanceRadius + .049f, "Accepted station leaves positive user-body clearance.");
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static string Argument(string name) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static string Hash(string path)
    {
        using (var hash = SHA256.Create()) using (var input = File.OpenRead(Path.GetFullPath(path)))
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
}
