using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Navigation-only reproduction of the recorded real-room boundary deadlock.</summary>
public static class ArdyRoomBoundaryRecoveryRegression
{
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";
    private static int checks;
    [Serializable] private sealed class Case { public string name; public bool passed; }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, sceneShaBefore, sceneShaAfter, navigationSha256, plannerSha256;
        public string scope = "Existing isolated copied SailingMoon environment, real NavMesh and Physics. Tests guard rejection and a short recovery certificate; no ARDY generation, avatar playback, source scene saving, or manual test scene.";
        public int checks, navigationSources, geometryProxies;
        public Vector3 recordedStart, projectedStart, recoveryTarget;
        public float recoveryLength, agentHeight, prefabScale;
        public ArdyRoomRouteDiagnostic startObservation, recoveryObservation;
        public List<Case> cases = new List<Case>();
    }
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the isolated validation project without Play mode.");
        string[] args = Environment.GetCommandLineArgs(); int outputIndex = Array.IndexOf(args, "-ardyRoomOutput");
        string output = outputIndex >= 0 ? args[outputIndex + 1] : "Logs/ardy-boundary-recovery";
        Directory.CreateDirectory(output);
        checks = 0;
        var report = new Report { sceneShaBefore = Hash(ScenePath),
            navigationSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs"),
            plannerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomApproachPlanner.cs"),
            recordedStart = new Vector3(-2.67963767f, -.69721955f, -4.99569035f) };
        Scene scene = default;
        GameObject host = null, avatar = null, user = null, obstruction = null;
        ArdyRoomNavigation navigation = null;
        try
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var living = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Collider>(true)).Single(c => c.name == "リビング床");
            Transform environment = living.transform.root;
            var water = environment.GetComponentsInChildren<Transform>(true).Single(t => t.name == "水面");
            host = New(scene, "BoundaryRecoveryRegressionHost");
            avatar = New(scene, "ExcludedBoundaryRecoveryAvatar");
            user = New(scene, "ObservedBoundaryRecoveryUser");
            user.transform.position = new Vector3(-1.9f, .7f, -5.9f);
            avatar.transform.position = report.recordedStart;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NeEEvA.vrm");
            var imported = prefab.GetComponentInChildren<Vrm10Instance>(true);
            var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, "Assets/Model/NeEEvA.vrm");
            report.prefabScale = imported.transform.lossyScale.y;
            navigation = host.AddComponent<ArdyRoomNavigation>();
            navigation.ExcludedVisualRoots = new[] { water };
            navigation.AgentHeight = Mathf.Max(1.1f, (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * report.prefabScale + .18f);
            Check(report, navigation.Build(environment, avatar.transform, out string reason), "build recorded real-room geometry: " + reason);
            report.agentHeight = navigation.AgentHeight; report.navigationSources = navigation.SourceCount; report.geometryProxies = navigation.GeometryProxyCount;
            bool valid = navigation.TryValidatePlanStart(report.recordedStart, out var start, out reason, out var startObservation);
            report.projectedStart = start; report.startObservation = startObservation;
            Check(report, !valid && startObservation.stage == "start" && startObservation.code == "navmesh-boundary", "recorded stuck start has precise boundary failure");
            Check(report, !ArdyRoomApproachPlanner.TryPlan(navigation, report.recordedStart, user.transform, 1f, out _, out _, out var approach) &&
                approach.stage == "start" && approach.code == "navmesh-boundary" && approach.preflightFailure?.code == "navmesh-boundary" && approach.candidateCount == 0,
                "start failure is reported before testing unrelated user candidates");
            Check(report, navigation.TryPlanBoundaryRecovery(start, out var recovery, out reason, out var recoveryObservation), "certify physically guarded inward route: " + reason);
            report.recoveryObservation = recoveryObservation; report.recoveryTarget = recovery.Target; report.recoveryLength = recovery.Route.Length;
            Check(report, recovery.Route.Length > .005f && Horizontal(recovery.Target - start).magnitude <= .12001f &&
                Vector3.Distance(recovery.Start, start) < .00001f, "recovery preserves actual start and stays within 12 cm");
            Check(report, !navigation.ValidateStep(start, start, out _, out _), "ordinary zero-step guard rejects unplannable start");
            Check(report, !navigation.ValidateStep(recovery.Target, start, out _, out _), "ordinary movement cannot enter known unreplannable point");
            Check(report, navigation.TryValidatePlanStart(recovery.Target, out _, out _, out _), "recovery destination is an ordinary valid start");
            int steps = Mathf.CeilToInt(Vector3.Distance(start, recovery.Target) / .009f);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 from = Vector3.Lerp(start, recovery.Target, (i - 1f) / steps);
                    Vector3 to = Vector3.Lerp(start, recovery.Target, (float)i / steps);
                    Check(report, navigation.ValidateBoundaryRecoveryStep(from, to, recovery, out _, out reason),
                        (pass == 0 ? "candidate audit " : "repeated playback ") + i + ": " + reason);
                }
            Check(report, !navigation.ValidateBoundaryRecoveryStep(recovery.Target, start, recovery, out _, out _), "recovery cannot reverse outward or jump to start");
            Vector3 side = Vector3.Cross(Horizontal(recovery.Target - start).normalized, Vector3.up) * .05f;
            Check(report, !navigation.ValidateBoundaryRecoveryStep(start + side, start + side, recovery, out _, out _), "recovery cannot be reused for a parallel outside corridor");
            Vector3 beyond = start + Horizontal(recovery.Target - start).normalized * .121f;
            Check(report, !navigation.ValidateBoundaryRecoveryStep(beyond, beyond, recovery, out _, out _), "recovery has a hard 12 cm extent");
            Check(report, !navigation.ValidateBoundaryRecoveryStep(start, start + Vector3.up * .06f, recovery, out _, out _), "recovery cannot change floors");
            var routeCopy = recovery.Route; routeCopy.Corners[0] += Vector3.right;
            Check(report, recovery.Route.Corners[0] == recovery.Start && navigation.ValidateBoundaryRecoveryStep(start, start, recovery, out _, out _), "caller cannot mutate the certified route");
            float radius = navigation.AgentRadius; navigation.AgentRadius += .001f;
            Check(report, !navigation.ValidateBoundaryRecoveryStep(start, start, recovery, out _, out _), "settings changes invalidate the certificate");
            navigation.AgentRadius = radius;
            var foreignHost = New(scene, "ForeignBoundaryRecoveryNavigation");
            foreignHost.transform.SetParent(host.transform, true);
            var foreign = foreignHost.AddComponent<ArdyRoomNavigation>();
            foreign.ExcludedVisualRoots = new[] { water }; foreign.AgentHeight = navigation.AgentHeight;
            Check(report, foreign.Build(environment, avatar.transform, out reason), "build distinct navigation for certificate isolation: " + reason);
            Check(report, !foreign.ValidateBoundaryRecoveryStep(start, start, recovery, out _, out _), "another navigation instance cannot use the certificate");
            foreign.Clear(); Object.DestroyImmediate(foreignHost);
            obstruction = New(scene, "DynamicBoundaryRecoveryObstruction");
            obstruction.transform.position = Vector3.Lerp(start, recovery.Target, .5f) + Vector3.up * .6f;
            obstruction.AddComponent<BoxCollider>().size = new Vector3(.03f, 1.2f, .03f);
            Physics.SyncTransforms();
            Check(report, !navigation.ValidateBoundaryRecoveryStep(start, Vector3.Lerp(start, recovery.Target, .1f), recovery, out _, out _), "new physical obstruction invalidates recovery playback");
            Check(report, !navigation.TryPlanBoundaryRecovery(start, out _, out _, out var blockedRecovery) && blockedRecovery.code != "bounded-inward-recovery", "a physical obstacle cannot receive a recovery certificate");
            Object.DestroyImmediate(obstruction); obstruction = null; Physics.SyncTransforms();
            Check(report, !navigation.TryPlanBoundaryRecovery(recovery.Target, out _, out _, out var notNeeded) && notNeeded.code == "recovery-not-needed", "already valid start does not request recovery");
            Check(report, avatar.transform.position == report.recordedStart, "navigation never moves or snaps the avatar");
            navigation.Clear();
            Check(report, !navigation.ValidateBoundaryRecoveryStep(start, start, recovery, out _, out _), "cleared navigation invalidates the certificate");
            Check(report, navigation.Build(environment, avatar.transform, out reason), "rebuild succeeds: " + reason);
            Check(report, !navigation.ValidateBoundaryRecoveryStep(start, start, recovery, out _, out _), "rebuilt navigation cannot reuse an old certificate");
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        finally
        {
            if (navigation != null) navigation.Clear();
            foreach (var obj in new[] { obstruction, user, avatar, host }) if (obj != null) Object.DestroyImmediate(obj);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            report.sceneShaAfter = Hash(ScenePath); report.checks = checks;
            if (report.sceneShaBefore != report.sceneShaAfter) { report.status = "failed"; report.error = "Scene file changed unexpectedly."; }
            File.WriteAllText(Path.Combine(output, "boundary-recovery.json"), JsonUtility.ToJson(report, true));
        }
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }
    private static GameObject New(Scene scene, string name)
    { var value = new GameObject(name) { hideFlags = HideFlags.DontSave }; SceneManager.MoveGameObjectToScene(value, scene); return value; }
    private static Vector3 Horizontal(Vector3 value) { value.y = 0; return value; }
    private static void Check(Report report, bool okay, string name)
    { checks++; report.cases.Add(new Case { name = name, passed = okay }); if (!okay) throw new InvalidOperationException(name); }
    private static string Hash(string path)
    { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
}
