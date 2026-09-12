using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Production navigation against the copied real environment; no source scene is saved.</summary>
public static class ArdyRoomActualRoomRegression
{
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";
    [Serializable] private sealed class Report
    {
        public string status = "running", error, scene = ScenePath, unityVersion, sceneSha256, navigationSha256, harnessSha256;
        public string scope = "Real imported SailingMoon environment, Physics and production ArdyRoomNavigation. No ARDY inference, avatar playback, chat, visual naturalness, stairs gait or complete building acceptance.";
        public int checks, failedChecks, geometryProxies, thinCovers, navigationSources, missingColliderMeshes;
        public float livingFloorY, mainFloorY, livingToMainHeightDifference;
        public Vector3 originalChatGround;
        public bool originalChatPositionUsable;
        public string originalChatPositionReason;
        public Vector3[] validLivingSamples;
        public List<MeshDetail> importedModelMeshes = new List<MeshDetail>();
        public Vector3 carpetScale;
        public Quaternion carpetRotation;
        public bool carpetMeshReadable;
        public string[] explicitNonSolidVisuals;
        public string[] thinCoverDiagnostics;
        public List<SampleFailure> sampleFailures = new List<SampleFailure>();
        public List<ProxyDetail> temporaryProxies = new List<ProxyDetail>();
        public List<Result> cases = new List<Result>();
    }
    [Serializable] private sealed class SampleFailure { public Vector3 point; public string reason; }
    [Serializable] private sealed class ProxyDetail { public string name; public Vector3 center, size; }
    [Serializable] private sealed class MeshDetail
    {
        public string path, name, guid;
        public long localFileId;
        public bool readable;
        public int vertices;
        public Vector3 boundsCenter, boundsSize;
    }
    [Serializable] private sealed class Result
    {
        public string name, expectation, reason;
        public bool passed, planAccepted;
        public Vector3 start, target;
        public Vector3[] corners;
        public float length, minimumTableClearance;
        public int validatedSteps;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use the isolated validation project in batch mode.");
        string output = Environment.GetEnvironmentVariable("ARDY_ROOM_ACTUAL_OUTPUT") ?? Path.GetFullPath("ArdyRoomActualRoomRegression");
        Directory.CreateDirectory(output);
        var report = new Report { unityVersion = Application.unityVersion, sceneSha256 = Hash(ScenePath),
            navigationSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs"),
            harnessSha256 = Hash("Assets/Editor/ArdyRoomActualRoomRegression.cs") };
        Scene scene = default;
        ArdyRoomNavigation navigation = null;
        try
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var originalRoots = scene.GetRootGameObjects();
            var root = new GameObject("ActualRoomReadOnlyFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            foreach (var item in originalRoots) item.transform.SetParent(root.transform, true);
            var avatar = new GameObject("ExcludedFixtureAvatar");
            SceneManager.MoveGameObjectToScene(avatar, scene);
            avatar.transform.position = new Vector3(-2.9f, -.72f, -4.6f);
            navigation = root.AddComponent<ArdyRoomNavigation>();
            string exclusions = Environment.GetEnvironmentVariable("ARDY_ROOM_ACTUAL_NON_SOLID_NAMES") ?? "";
            report.explicitNonSolidVisuals = exclusions.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            navigation.ExcludedVisualRoots = root.GetComponentsInChildren<Transform>(true)
                .Where(t => report.explicitNonSolidVisuals.Contains(t.name)).ToArray();
            var colliders = root.GetComponentsInChildren<Collider>(true);
            report.missingColliderMeshes = colliders.OfType<MeshCollider>().Count(c => c.sharedMesh == null);
            foreach (string file in new[] { "room_main.fbx", "room_basement.fbx", "carpet.fbx" })
            {
                string path = "Assets/Private/SailingMoon/Source/モサンゴ屋/SailingMoon/FBX/" + file;
                foreach (Mesh mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>())
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long id);
                    report.importedModelMeshes.Add(new MeshDetail { path = path, name = mesh.name, guid = guid, localFileId = id,
                        readable = mesh.isReadable, vertices = mesh.vertexCount, boundsCenter = mesh.bounds.center, boundsSize = mesh.bounds.size });
                }
            }
            var carpet = root.GetComponentsInChildren<MeshRenderer>(true).First(r => r.name == "carpet");
            report.carpetScale = carpet.transform.lossyScale;
            report.carpetRotation = carpet.transform.rotation;
            report.carpetMeshReadable = carpet.GetComponent<MeshFilter>().sharedMesh.isReadable;
            Collider living = colliders.Single(c => c.name == "リビング床");
            Collider main = colliders.Single(c => c.name == "1階床");
            var table = root.GetComponentsInChildren<MeshRenderer>(true).Single(r => r.name == "lowtable");
            Physics.SyncTransforms();
            Require(navigation.Build(root.transform, avatar.transform, out string reason), "Build actual room: " + reason);
            report.geometryProxies = navigation.GeometryProxyCount;
            report.thinCovers = navigation.ThinCoverCount;
            report.thinCoverDiagnostics = navigation.ThinCoverDiagnostics.ToArray();
            report.navigationSources = navigation.SourceCount;
            foreach (var collider in scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Collider>(true)))
                if (collider.name.StartsWith("GeometryGuard_", StringComparison.Ordinal))
                    report.temporaryProxies.Add(new ProxyDetail { name = collider.name, center = collider.bounds.center, size = collider.bounds.size });
            Vector3 spawn = GroundOn(living, avatar.transform.position);
            report.originalChatGround = spawn;
            report.livingFloorY = spawn.y;
            Vector3 mainSample = GroundOn(main, new Vector3(-7f, 0f, -5f));
            report.mainFloorY = mainSample.y;
            report.livingToMainHeightDifference = mainSample.y - spawn.y;
            report.originalChatPositionUsable = navigation.TryPlan(spawn, spawn, out _, out reason);
            report.originalChatPositionReason = reason;

            var valid = new List<Vector3>();
            Bounds room = living.bounds;
            for (float x = room.min.x + .45f; x < room.max.x - .44f; x += .4f)
            for (float z = room.min.z + .45f; z < room.max.z - .44f; z += .4f)
            {
                Vector3 point = GroundOn(living, new Vector3(x, spawn.y, z));
                if (navigation.TryPlan(point, point, out _, out string sampleReason)) valid.Add(point);
                else report.sampleFailures.Add(new SampleFailure { point = point, reason = sampleReason });
            }
            report.validLivingSamples = valid.ToArray();
            Require(valid.Count > 1, "No usable living-room floor samples; inspect proxy coverage and ground plane.");
            Vector3 start = report.originalChatPositionUsable ? spawn : valid.OrderBy(p => (p - spawn).sqrMagnitude).First();
            ArdyRoomRoute best = null;
            Vector3 bestTarget = start;
            foreach (var target in valid.OrderByDescending(p => (p - start).sqrMagnitude))
            {
                if (Vector3.Distance(start, target) < 1f) continue;
                if (!navigation.TryPlan(start, target, out var route, out _)) continue;
                best = route; bestTarget = target; break;
            }
            var walk = new Result { name = "actual-living-room-same-level-route", expectation = "Complete route >= 1 metre, each execution increment guarded", start = start, target = bestTarget,
                planAccepted = best != null, passed = best != null && best.Length >= 1f };
            if (best != null)
            {
                walk.corners = best.Corners; walk.length = best.Length;
                ValidateRoute(navigation, best, walk);
            }
            else walk.reason = "No complete >= 1 metre route among real supported floor samples.";
            Add(report, walk);

            Vector3 tableGround = GroundOn(living, table.bounds.center);
            bool tableAccepted = navigation.TryPlan(start, tableGround, out var tableRoute, out reason);
            Add(report, new Result { name = "actual-colliderless-coffee-table-target", expectation = "Reject body target inside visible coffee table", start = start, target = tableGround,
                planAccepted = tableAccepted, passed = !tableAccepted, reason = reason, corners = tableRoute?.Corners });

            bool mainAccepted = navigation.TryPlan(start, mainSample, out var mainRoute, out reason);
            Add(report, new Result { name = "actual-sunken-living-to-main-floor", expectation = "Reject approximately 0.73 metre floor-height change", start = start, target = mainSample,
                planAccepted = mainAccepted, passed = !mainAccepted && report.livingToMainHeightDifference > .6f, reason = reason, corners = mainRoute?.Corners });

            // The living floor collider ends at X=-6 beside the raised main floor. This
            // point still has centre support but its full body footprint crosses that edge.
            Vector3 edge = GroundOn(living, new Vector3(room.min.x + .04f, spawn.y, -3.5f));
            bool edgeAccepted = navigation.TryPlan(start, edge, out var edgeRoute, out reason);
            Add(report, new Result { name = "actual-sunken-floor-edge-footprint", expectation = "Reject insufficient same-level support at the boundary", start = start, target = edge,
                planAccepted = edgeAccepted, passed = !edgeAccepted, reason = reason, corners = edgeRoute?.Corners });

            // Find real supported endpoints straddling the coffee-table footprint. A
            // complete guarded detour or a clear rejection are both valid; cutting through is not.
            Bounds padded = table.bounds; padded.Expand(new Vector3(navigation.AgentRadius * 2f, 0f, navigation.AgentRadius * 2f));
            Result furniture = null;
            foreach (Vector3 a in valid)
            {
                foreach (Vector3 b in valid)
                {
                    if (Vector3.Distance(a, b) < 1f || !CrossesXZ(a, b, padded)) continue;
                    bool accepted = navigation.TryPlan(a, b, out var route, out reason);
                    var candidate = new Result { name = "actual-coffee-table-detour-or-rejection", expectation = "No body route through table; complete guarded detour or explicit rejection", start = a, target = b,
                        planAccepted = accepted, passed = !accepted, reason = reason, corners = route?.Corners, length = route?.Length ?? 0f };
                    if (accepted)
                    {
                        candidate.passed = true; ValidateRoute(navigation, route, candidate);
                        candidate.minimumTableClearance = TableClearance(route, table.bounds);
                        if (candidate.minimumTableClearance < navigation.AgentRadius - .005f)
                        { candidate.passed = false; candidate.reason = "Independent table AABB clearance is smaller than the body radius."; }
                    }
                    furniture = candidate;
                    if (accepted) break;
                }
                if (furniture != null && furniture.planAccepted) break;
            }
            if (furniture != null) Add(report, furniture);

            Add(report, new Result { name = "existing-chat-placement-usable", expectation = "Current chat rig can begin a guarded route without teleportation", start = spawn, target = spawn,
                planAccepted = report.originalChatPositionUsable, passed = report.originalChatPositionUsable, reason = report.originalChatPositionReason });
            report.status = report.failedChecks == 0 ? "passed" : "failed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); }
        finally
        {
            if (navigation != null) navigation.Clear();
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        }
        Debug.Log("[ArdyRoomActualRoomRegression] " + report.status + " cases=" + report.checks + " failed=" + report.failedChecks + " output=" + output);
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }

    private static void ValidateRoute(ArdyRoomNavigation navigation, ArdyRoomRoute route, Result result)
    {
        for (int i = 1; i < route.Corners.Length; ++i)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(route.Corners[i - 1], route.Corners[i]) / .08f));
            for (int step = 1; step <= count; ++step)
            {
                Vector3 before = Vector3.Lerp(route.Corners[i - 1], route.Corners[i], (step - 1f) / count);
                Vector3 after = Vector3.Lerp(route.Corners[i - 1], route.Corners[i], (float)step / count);
                result.validatedSteps++;
                if (navigation.ValidateStep(before, after, out float ground, out string reason) && Mathf.Abs(ground - route.GroundY) <= navigation.MaxRouteHeightVariation) continue;
                result.passed = false; result.reason = reason ?? "Execution support changed plane."; return;
            }
        }
    }

    private static bool CrossesXZ(Vector3 a, Vector3 b, Bounds bounds)
    {
        var delta = b - a; float low = 0f, high = 1f;
        for (int axis = 0; axis < 3; axis += 2)
        {
            if (Mathf.Abs(delta[axis]) < .00001f) { if (a[axis] < bounds.min[axis] || a[axis] > bounds.max[axis]) return false; continue; }
            float first = (bounds.min[axis] - a[axis]) / delta[axis];
            float last = (bounds.max[axis] - a[axis]) / delta[axis];
            if (first > last) { float swap = first; first = last; last = swap; }
            low = Mathf.Max(low, first); high = Mathf.Min(high, last);
            if (low > high) return false;
        }
        return high > .01f && low < .99f;
    }

    private static float TableClearance(ArdyRoomRoute route, Bounds table)
    {
        float minimum = float.MaxValue;
        for (int i = 1; i < route.Corners.Length; ++i)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(route.Corners[i - 1], route.Corners[i]) / .03f));
            for (int step = 0; step <= count; ++step)
            {
                Vector3 point = Vector3.Lerp(route.Corners[i - 1], route.Corners[i], (float)step / count);
                float x = Mathf.Max(table.min.x - point.x, 0f, point.x - table.max.x);
                float z = Mathf.Max(table.min.z - point.z, 0f, point.z - table.max.z);
                minimum = Mathf.Min(minimum, Mathf.Sqrt(x * x + z * z));
            }
        }
        return minimum;
    }

    private static Vector3 GroundOn(Collider collider, Vector3 point)
    {
        var origin = new Vector3(point.x, collider.bounds.max.y + .15f, point.z);
        if (!collider.Raycast(new Ray(origin, Vector3.down), out RaycastHit hit, collider.bounds.size.y + .3f))
            throw new InvalidOperationException("Expected floor ray missed " + collider.name + " at " + point);
        return hit.point;
    }
    private static void Add(Report report, Result result) { report.cases.Add(result); report.checks++; if (!result.passed) report.failedChecks++; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
}
