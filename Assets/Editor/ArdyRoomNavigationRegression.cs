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

/// <summary>Disposable real Physics/NavMesh fixtures. Does not replace a user's room or run ARDY inference.</summary>
public static class ArdyRoomNavigationRegression
{
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, navigationSha256, harnessSha256;
        public string scope = "Real Unity Physics colliders and dynamically built NavMesh; synthetic flat rooms, furniture, barriers and unsafe terrain. No visual/ARDY quality, real-house acceptance, humanoid limb collision or navigation latency claim.";
        public string parameters = "Body radius 0.30 m; height 1.70 m; slope <= 3 degrees; original local support height range <= 0.025 m; route elevation within 0.04 m of start; samples <= 0.06 m apart; body floor clearance 0.015 m. Broad real thin mesh covers <= 0.0351 m thick can receive <= 0.03 m surface tolerance only with original floor immediately beneath. Flat-floor gait only; stairs, climbing and drops are rejected.";
        public int checks;
        public List<Result> fixtures = new List<Result>();
    }
    [Serializable] private sealed class Result
    {
        public string name, outcome, reason;
        public int sourceCount, cornerCount;
        public int geometryProxyCount;
        public float routeLength;
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Root, Avatar;
        public readonly ArdyRoomNavigation Navigation;
        private readonly List<GameObject> outside = new List<GameObject>();
        public Fixture(string name)
        {
            // RunBatch is restricted to a dedicated isolated batch process. Each fixture replaces
            // its preceding empty fixture scene; no additive untitled-scene prompt is involved.
            Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Root = new GameObject("NavigationFixture_" + name);
            SceneManager.MoveGameObjectToScene(Root, Scene);
            Avatar = new GameObject("AvatarExcludedFromBake");
            Avatar.transform.SetParent(Root.transform, false);
            var capsule = Avatar.AddComponent<CapsuleCollider>();
            capsule.radius = .28f; capsule.height = 1.7f; capsule.center = new Vector3(0f, .85f, 0f);
            Navigation = Root.AddComponent<ArdyRoomNavigation>();
        }
        public BoxCollider Box(string name, Vector3 center, Vector3 size, bool external = false)
        {
            var item = new GameObject(name);
            if (external) { SceneManager.MoveGameObjectToScene(item, Scene); outside.Add(item); }
            else item.transform.SetParent(Root.transform, false);
            item.transform.position = center;
            var box = item.AddComponent<BoxCollider>(); box.size = size;
            return box;
        }
        public BoxCollider Floor(float size = 12f) => Box("Floor", new Vector3(0f, -.1f, 0f), new Vector3(size, .2f, size));
        public void Build() { Check(Navigation.Build(Root.transform, Avatar.transform, out var reason), "Build: " + reason); }
        public void Dispose()
        {
            Navigation.Clear();
            Object.DestroyImmediate(Root);
            foreach (var item in outside) if (item != null) Object.DestroyImmediate(item);
            // Keep the last empty scene loaded; the next fixture replaces it in this batch process.
        }
    }
    private static int checks;

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use only the isolated unity-naturalness-validation project in batch mode.");
        var output = Path.GetFullPath(Argument("-ardyRoomNavigationOutput") ?? Path.Combine(Application.dataPath, "../Logs/ardy-room-navigation-regression"));
        Directory.CreateDirectory(output);
        var report = new Report { unityVersion = Application.unityVersion,
            navigationSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs"),
            harnessSha256 = Hash("Assets/Editor/ArdyRoomNavigationRegression.cs") };
        checks = 0;
        int settingsBefore = NavMesh.GetSettingsCount();
        try
        {
            ExerciseFlat(report);
            ExerciseWall(report);
            ExerciseUnsafeTerrain(report);
            ExerciseDynamicGuards(report);
            ExerciseColliderlessFurniture(report);
            ExerciseThinCover(report);
            ExerciseVisualExclusion(report);
            ExerciseExclusions(report);
            Check(NavMesh.GetSettingsCount() == settingsBefore, "Owned NavMesh agent settings must be removed after fixtures");
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks;
        File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[ArdyRoomNavigationRegression] " + report.status + " " + checks + " checks: " + output);
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }

    private static void ExerciseFlat(Report report)
    {
        using (var f = new Fixture("flat-detour"))
        {
            f.Floor();
            f.Box("Furniture", new Vector3(0f, .7f, 0f), new Vector3(1.2f, 1.4f, 2f));
            f.Avatar.transform.position = new Vector3(-3f, 0f, 0f);
            f.Build();
            Check(f.Navigation.SourceCount == 2, "Avatar collider must not enter bake");
            Check(f.Navigation.TryPlan(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), out var route, out var reason), "Furniture detour: " + reason);
            Check(route.Corners.Length >= 3 && route.Length > 6.05f, "Real route must detour around furniture");
            for (int i = 1; i < route.Corners.Length; i++)
            {
                int count = Mathf.CeilToInt(Vector3.Distance(route.Corners[i - 1], route.Corners[i]) / .1f);
                for (int n = 1; n <= count; n++)
                {
                    var before = Vector3.Lerp(route.Corners[i - 1], route.Corners[i], (float)(n - 1) / count);
                    var after = Vector3.Lerp(route.Corners[i - 1], route.Corners[i], (float)n / count);
                    Check(f.Navigation.ValidateStep(before, after, out var ground, out reason), "Every detour execution step: " + reason);
                    Check(Mathf.Abs(ground) < .005f, "Actual floor remains level");
                }
            }
            report.fixtures.Add(new Result { name = "flat-furniture-detour", outcome = "complete and all incremental steps clear",
                cornerCount = route.Corners.Length, routeLength = route.Length, sourceCount = f.Navigation.SourceCount });
            Check(!f.Navigation.ValidateStep(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), out _, out _), "Reject a teleport-sized displacement");
            Check(!f.Navigation.TryPlan(new Vector3(-3f, 0f, 0f), new Vector3(float.NaN, 0f, 0f), out _, out _), "Reject nonfinite target");
            f.Navigation.Clear();
            Check(!f.Navigation.IsBuilt && !f.Navigation.ValidateStep(Vector3.zero, Vector3.zero, out _, out _), "Clear removes ability to execute");
        }
    }

    private static void ExerciseWall(Report report)
    {
        using (var f = new Fixture("sealed-wall"))
        {
            f.Floor(8f);
            f.Box("WallAcrossFloor", new Vector3(0f, 1f, 0f), new Vector3(.2f, 2f, 9f));
            f.Build();
            RejectPlan(report, f, "unreachable-target-behind-wall", new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f));
            RejectPlan(report, f, "target-inside-wall-no-nearest-snap", new Vector3(-2f, 0f, 0f), Vector3.zero);
        }
    }

    private static void ExerciseUnsafeTerrain(Report report)
    {
        using (var f = new Fixture("stairs"))
        {
            f.Floor();
            f.Box("Step", new Vector3(2f, .10f, 0f), new Vector3(2f, .20f, 4f));
            f.Build();
            RejectPlan(report, f, "raised-platform", new Vector3(-2f, 0f, 0f), new Vector3(2f, .2f, 0f));
        }
        using (var f = new Fixture("gap"))
        {
            f.Box("LeftFloor", new Vector3(-2f, -.1f, 0f), new Vector3(3f, .2f, 6f));
            f.Box("RightFloor", new Vector3(2f, -.1f, 0f), new Vector3(3f, .2f, 6f));
            f.Build();
            RejectPlan(report, f, "unsupported-gap", new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f));
            RejectPlan(report, f, "edge-with-insufficient-footprint", new Vector3(-2f, 0f, 0f), new Vector3(-.55f, 0f, 0f));
        }
        using (var f = new Fixture("slope"))
        {
            var ramp = f.Box("Ramp15Degrees", Vector3.zero, new Vector3(8f, .2f, 6f));
            ramp.transform.rotation = Quaternion.Euler(0f, 0f, 15f);
            f.Build();
            Check(!f.Navigation.TryProjectGround(new Vector3(0f, .1036f, 0f), out _, out var reason), "Reject actual sloped support");
            report.fixtures.Add(new Result { name = "slope15degrees", outcome = "rejected", reason = reason });
        }
        using (var f = new Fixture("low-ceiling"))
        {
            f.Floor();
            f.Box("LowCeiling", new Vector3(2f, 1.4f, 0f), new Vector3(3f, .15f, 4f));
            f.Build();
            RejectPlan(report, f, "head-clearance", new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f));
        }
        using (var f = new Fixture("two-floors"))
        {
            f.Floor();
            f.Box("UpperFloor", new Vector3(0f, 2.9f, 0f), new Vector3(8f, .2f, 8f));
            f.Build();
            RejectPlan(report, f, "no-cross-storey-projection", Vector3.zero, new Vector3(0f, 3f, 0f));
            Check(!f.Navigation.TryProjectGround(new Vector3(0f, 1.5f, 0f), out _, out _), "No long downward ground search");
        }
    }

    private static void ExerciseDynamicGuards(Report report)
    {
        using (var f = new Fixture("dynamic-geometry"))
        {
            var floor = f.Floor();
            f.Build();
            Check(f.Navigation.TryPlan(Vector3.zero, new Vector3(2f, 0f, 0f), out _, out var reason), "Initial dynamic fixture plan: " + reason);
            var blocker = f.Box("UserOutsideEnvironment", new Vector3(.6f, .8f, 0f), new Vector3(.15f, 1.6f, .6f), true);
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.4f, 0f, 0f), out _, out reason), "Obstacle appearing after planning must block step");
            report.fixtures.Add(new Result { name = "dynamic-user-obstacle", outcome = "execution rejected", reason = reason });
            Object.DestroyImmediate(blocker.gameObject);
            var threshold = f.Box("NewDoorThreshold", new Vector3(.5f, .04f, 0f), new Vector3(.15f, .08f, 1f));
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.4f, 0f, 0f), out _, out reason), "New threshold absent from bake must block body sweep");
            report.fixtures.Add(new Result { name = "dynamic-door-threshold", outcome = "execution rejected", reason = reason });
            Object.DestroyImmediate(threshold.gameObject);
            floor.enabled = false;
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.2f, 0f, 0f), out _, out reason), "Removed supporting floor must block execution");
            report.fixtures.Add(new Result { name = "removed-floor", outcome = "execution rejected", reason = reason });
        }
        using (var f = new Fixture("narrow-threshold"))
        {
            f.Floor();
            f.Build();
            Check(f.Navigation.TryPlan(Vector3.zero, new Vector3(2f, 0f, 0f), out _, out _), "Threshold fixture initial route");
            f.Box("ThinWall", new Vector3(.42f, .9f, 0f), new Vector3(.005f, 1.8f, 1f));
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.3f, 0f, 0f), out _, out var reason), "Thin obstacles cannot be skipped by samples");
            report.fixtures.Add(new Result { name = "thin-wall-sweep", outcome = "execution rejected", reason = reason });
        }
    }

    private static void ExerciseExclusions(Report report)
    {
        using (var f = new Fixture("explicit-exclusions"))
        {
            f.Floor();
            var user = f.Box("UserNestedInsideEnvironment", new Vector3(2f, .85f, 0f), new Vector3(.5f, 1.7f, .5f));
            var trigger = f.Box("NonSolidTrigger", Vector3.zero, Vector3.one * 4f); trigger.isTrigger = true;
            f.Navigation.IgnoredRoots = new[] { user.transform };
            f.Build();
            Check(f.Navigation.SourceCount == 1, "Only floor enters bake; explicit user, avatar and trigger do not");
            Check(f.Navigation.TryPlan(Vector3.zero, new Vector3(1f, 0f, 0f), out _, out var reason), "Avatar and trigger do not obstruct valid route: " + reason);
            Check(!f.Navigation.ValidateStep(new Vector3(1.3f, 0f, 0f), new Vector3(1.6f, 0f, 0f), out _, out reason), "Bake-excluded user remains an execution obstacle");
            report.fixtures.Add(new Result { name = "explicit-source-and-collision-exclusions", outcome = "floor only in bake; user still blocks body", sourceCount = f.Navigation.SourceCount, reason = reason });
        }
    }

    private static void ExerciseColliderlessFurniture(Report report)
    {
        using (var f = new Fixture("colliderless-furniture"))
        {
            f.Floor();
            var furniture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            furniture.name = "VisibleTableWithoutCollider";
            furniture.transform.SetParent(f.Root.transform, false);
            furniture.transform.position = new Vector3(0f, .5f, 0f);
            furniture.transform.localScale = new Vector3(1f, 1f, 2f);
            Object.DestroyImmediate(furniture.GetComponent<Collider>());
            f.Build();
            Check(f.Navigation.GeometryProxyCount == 1 && f.Navigation.SourceCount == 2, "Colliderless visible mesh gets one owned obstacle proxy");
            Check(f.Navigation.TryPlan(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), out var route, out var reason), "Colliderless furniture detour: " + reason);
            Check(route.Corners.Length >= 3 && route.Length > 6.05f, "Planner detours around colliderless furniture");
            Check(!f.Navigation.TryProjectGround(new Vector3(0f, 1f, 0f), out _, out _), "Conservative mesh proxy is never invented walkable floor support");
            furniture.transform.position = new Vector3(-2.4f, .5f, 0f);
            Check(!f.Navigation.ValidateStep(new Vector3(-3f, 0f, 0f), new Vector3(-2.8f, 0f, 0f), out _, out reason), "Proxy follows moved source renderer bounds before step");
            report.fixtures.Add(new Result { name = "colliderless-furniture", outcome = "owned conservative proxy detour and live obstruction",
                reason = reason, geometryProxyCount = f.Navigation.GeometryProxyCount, sourceCount = f.Navigation.SourceCount,
                routeLength = route.Length, cornerCount = route.Corners.Length });
            f.Navigation.Clear();
            Check(f.Navigation.GeometryProxyCount == 0, "Temporary geometry proxies are released");
            Check(furniture.GetComponent<Collider>() == null, "Original room mesh remains unchanged");
        }
    }

    private static void ExerciseThinCover(Report report)
    {
        using (var f = new Fixture("thin-real-mesh-cover"))
        {
            var floor = f.Floor();
            var rug = BareCube(f, "AnonymousBroadCover", new Vector3(0f, .011f, 0f), new Vector3(3f, .03f, 4f));
            f.Build();
            Check(f.Navigation.ThinCoverCount == 1, "Thin broad actual mesh over existing floor receives a verified cover collider");
            Check(f.Navigation.TryProjectGround(Vector3.zero, out var projected, out var reason) && Mathf.Abs(projected.y - .026f) < .001f,
                "Projection uses real cover top, not invented AABB floor: " + reason);
            Check(f.Navigation.TryPlan(Vector3.zero, new Vector3(3f, 0f, 0f), out var route, out reason), "Route can leave supported thin cover: " + reason);
            for (int i = 0; i < 30; i++)
                Check(f.Navigation.ValidateStep(new Vector3(i * .1f, 0f, 0f), new Vector3((i + 1) * .1f, 0f, 0f), out _, out reason),
                    "Thin cover edge does not become a false body obstacle: " + reason);
            floor.enabled = false;
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.1f, 0f, 0f), out _, out reason), "Cover cannot support a body when underlying original floor disappears");
            floor.enabled = true;
            rug.transform.position += Vector3.up * .15f;
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.1f, 0f, 0f), out _, out reason), "Moved elevated cover becomes an obstacle until rebuilt");
            report.fixtures.Add(new Result { name = "supported-thin-cover", outcome = "real top and edge accepted; removed support/moved cover rejected", routeLength = route.Length, reason = reason });
        }
        using (var f = new Fixture("floating-thin-cover"))
        {
            f.Floor();
            BareCube(f, "AnonymousFloatingBoard", new Vector3(0f, .215f, 0f), new Vector3(3f, .03f, 3f));
            f.Build();
            Check(f.Navigation.ThinCoverCount == 0, "Thin mesh suspended above floor cannot become walkable support");
            RejectPlan(report, f, "floating-thin-board", new Vector3(-3f, 0f, 0f), new Vector3(0f, .23f, 0f));
        }
        using (var f = new Fixture("thin-bevel-over-flat-floor"))
        {
            f.Floor();
            var item = new GameObject("AnonymousThinBeveledMesh");
            item.transform.SetParent(f.Root.transform, false);
            var mesh = new Mesh();
            mesh.vertices = new[] {
                new Vector3(-1,.002f,-1), new Vector3(-.8f,.025f,-1), new Vector3(.8f,.025f,-1), new Vector3(1,.002f,-1),
                new Vector3(-1,.002f,1), new Vector3(-.8f,.025f,1), new Vector3(.8f,.025f,1), new Vector3(1,.002f,1) };
            mesh.triangles = new[] { 0,4,1,1,4,5, 1,5,2,2,5,6, 2,6,3,3,6,7 };
            mesh.RecalculateBounds(); mesh.RecalculateNormals();
            item.AddComponent<MeshFilter>().sharedMesh = mesh; item.AddComponent<MeshRenderer>();
            f.Build();
            Check(f.Navigation.ThinCoverCount == 1, "Broad 2.5 cm cover over actual flat floor is supported");
            Check(f.Navigation.TryPlan(new Vector3(-.9f,0,0), new Vector3(1.5f,0,0), out var route, out var reason),
                "Small cover bevel is not a full terrain slope: " + reason);
            Check(f.Navigation.ValidateStep(new Vector3(-.9f,0,0), new Vector3(-.6f,0,0), out _, out reason),
                "Step over supported shallow bevel: " + reason);
            report.fixtures.Add(new Result { name = "thin-cover-bevel-over-flat-floor", outcome = "bounded surface relief accepted over verified flat floor", routeLength = route.Length });
            f.Navigation.Clear(); Object.DestroyImmediate(mesh);
        }
        using (var f = new Fixture("thin-bridge-gap"))
        {
            f.Box("LeftFloor", new Vector3(-2f, -.1f, 0f), new Vector3(3f, .2f, 6f));
            f.Box("RightFloor", new Vector3(2f, -.1f, 0f), new Vector3(3f, .2f, 6f));
            BareCube(f, "AnonymousThinBridge", new Vector3(0f, .011f, 0f), new Vector3(3f, .03f, 3f));
            f.Build();
            Check(f.Navigation.ThinCoverCount == 0, "Broad thin mesh spanning a gap lacks continuous original support");
            RejectPlan(report, f, "thin-cover-over-gap", new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f));
        }
        using (var f = new Fixture("thin-narrow-threshold"))
        {
            f.Floor();
            BareCube(f, "AnonymousNarrowThreshold", new Vector3(0f, .014f, 0f), new Vector3(.15f, .028f, 3f));
            f.Build();
            Check(f.Navigation.ThinCoverCount == 0, "Narrow threshold does not acquire broad floor-cover allowance");
            Check(f.Navigation.TryPlan(new Vector3(-2f, 0f, 0f), new Vector3(-1f, 0f, 0f), out _, out _), "Threshold fixture obtains a safe initial plan");
            Check(!f.Navigation.ValidateStep(new Vector3(-.4f, 0f, 0f), new Vector3(-.1f, 0f, 0f), out _, out var reason), "Thin narrow threshold remains a body obstruction");
            report.fixtures.Add(new Result { name = "thin-narrow-threshold", outcome = "cover allowance rejected", reason = reason });
        }
        using (var f = new Fixture("thick-cover"))
        {
            f.Floor();
            BareCube(f, "AnonymousThickThreshold", new Vector3(0f, .04f, 0f), new Vector3(2f, .08f, 3f));
            f.Build();
            Check(f.Navigation.ThinCoverCount == 0, "Thick threshold cannot acquire thin-cover allowance");
            RejectPlan(report, f, "thick-mesh-threshold", new Vector3(-3f, 0f, 0f), new Vector3(0f, .08f, 0f));
        }
    }

    private static GameObject BareCube(Fixture fixture, string name, Vector3 center, Vector3 size)
    {
        var item = GameObject.CreatePrimitive(PrimitiveType.Cube);
        item.name = name;
        item.transform.SetParent(fixture.Root.transform, false);
        item.transform.position = center;
        item.transform.localScale = size;
        Object.DestroyImmediate(item.GetComponent<Collider>());
        return item;
    }

    private static void ExerciseVisualExclusion(Report report)
    {
        using (var f = new Fixture("explicit-non-solid-visual"))
        {
            f.Floor();
            var visual = BareCube(f, "ExplicitlyDeclaredNonSolidSurface", new Vector3(0f, .07f, 0f), new Vector3(20f, .00002f, 20f));
            f.Navigation.ExcludedVisualRoots = new[] { visual.transform };
            f.Build();
            Check(f.Navigation.GeometryProxyCount == 0, "Explicit non-solid visuals do not become giant blocking AABB proxies");
            Check(f.Navigation.TryPlan(Vector3.zero, new Vector3(2f, 0f, 0f), out _, out var reason), "Original floor remains traversable beneath non-solid visual: " + reason);
            var solid = visual.AddComponent<BoxCollider>();
            solid.size = new Vector3(1f, 50000f, 1f);
            Check(!f.Navigation.ValidateStep(Vector3.zero, new Vector3(.1f, 0f, 0f), out _, out reason), "Visual exclusion must not ignore an actual physical collider");
            report.fixtures.Add(new Result { name = "explicit-non-solid-visual", outcome = "visual proxy excluded; real Collider still blocks", reason = reason });
        }
    }

    private static void RejectPlan(Report report, Fixture fixture, string name, Vector3 start, Vector3 target)
    {
        Check(!fixture.Navigation.TryPlan(start, target, out var route, out var reason) && route == null, name + " must not return partial/unsafe route");
        report.fixtures.Add(new Result { name = name, outcome = "rejected", reason = reason, sourceCount = fixture.Navigation.SourceCount });
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static string Argument(string name) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static string Hash(string path)
    {
        using (var hash = SHA256.Create()) using (var input = File.OpenRead(Path.GetFullPath(path)))
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
}
