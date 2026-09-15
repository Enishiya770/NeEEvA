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

/// <summary>Existing room geometry at the recorded op39 start/goal; fresh idle, one fixed native stream.</summary>
[InitializeOnLoad]
public static class ArdyRoomLowTableStreamRegression
{
    private const string Key = "NeEEvA.ARDY.LowTableStream.";
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";
    private const string AvatarPath = "Assets/Model/NeEEvA.vrm";
    private const string AvatarName = "ARDY isolated low table avatar";
    private static readonly Vector3 RecordedStart = new Vector3(-4.385425568f, -.697219551f, -6.749407768f);
    private static readonly Vector3 RecordedGoal = new Vector3(-2.447770596f, -.704214215f, -7.394775868f);
    private static readonly Vector3 RecordedUser = new Vector3(-1.394830108f, .448253453f, -6.819177151f);
    [Serializable] private sealed class Sample
    {
        public float seconds, desired, measured, buffer, rootStep, goalDistance;
        public int steps;
        public Vector3 root;
        public string state, stage;
    }
    [Serializable] private sealed class GeometryCase
    {
        public float heading, desired, length, firstWindowTravel;
        public int verifiedSamples, callbacks;
        public Vector3[] route;
        public string outcome, reason;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, sceneShaBefore, sceneShaAfter, trajectorySha, finalState, finalStatus, recordedSeamsReplayJson;
        public string scope = "Recorded op39 world start/goal in the existing copied SailingMoon room. Fresh project idle, fixed 180 degree root yaw; historical op39 history/heading were not captured. Geometry cases use controlled headings/speeds. Optional native stream is one operation with fixed seed, without automatic restart or alternative model retries by this harness. No main scene/camera changes.";
        public bool geometryOnly, everPlayed, returned, rootStillAdmitted, autonomousArrival, stayedIdle;
        public string stopPolicy = "Production continuous-follow arrival and smooth return; no harness Cancel until after evidence is saved.";
        public int checks, sources, proxies, steps, underruns;
        public float duration, travel, finalDistance, maximumRootStep;
        public Vector3 start = RecordedStart, goal = RecordedGoal, actualRoot;
        public List<GeometryCase> geometry = new List<GeometryCase>();
        public List<Sample> samples = new List<Sample>();
        public List<ArdyRoomLocomotionController.SegmentEvidence> segments;
        public ArdyRoomRejectedPrefixEvidence[] rejectedPrefixes;
    }
    private static Report report;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyRoomNavigation navigation;
    private static ArdyRoomLocomotionController controller;
    private static ArdyLocomotionAvatarReference reference;
    private static Vector3 previous;
    private static float started, updateAt, stoppedAt;
    private static int phase, lastFrame;

    static ArdyRoomLowTableStreamRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                try { Initialize(); } catch (Exception e) { Finish(e); }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) FinalizeBatch();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) FinalizeBatch();
        };
    }
    public static void RunBatch() => Setup(false);
    public static void RunGeometryBatch() => Setup(true);
    private static void Setup(bool geometryOnly)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only the existing isolated validation project is permitted.");
        SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-lowtable-stream")));
        SessionState.SetBool(Key + "GeometryOnly", geometryOnly);
        SessionState.SetString(Key + "RecordedSeams", Argument("-ardyRoomRecordedSeams", ""));
        SessionState.SetString(Key + "Hash", Hash(ScenePath));
        try
        {
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved isolated scene.");
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            foreach (var root in scene.GetRootGameObjects())
                foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            var environment = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Collider>(true)).Single(c => c.name == "リビング床").transform.root;
            ArdyRoomGeometryPreparation.Prepare(environment, false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var imported = prefab.GetComponentInChildren<Vrm10Instance>(true);
            var basis = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, AvatarPath);
            var actor = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene); actor.name = AvatarName;
            actor.transform.position = RecordedStart - Vector3.up * basis.groundY * imported.transform.lossyScale.y;
            actor.transform.rotation = Quaternion.Euler(0, 180, 0);
            foreach (var behaviour in actor.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (var owner in actor.GetComponentsInChildren<Animator>(true))
            { owner.runtimeAnimatorController = null; owner.applyRootMotion = false; owner.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 140);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e) { Finish(e); }
    }
    private static void Initialize()
    {
        report = new Report { geometryOnly = SessionState.GetBool(Key + "GeometryOnly", false), sceneShaBefore = SessionState.GetString(Key + "Hash", ""),
            trajectorySha = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomStreamTrajectory.cs") };
        avatar = GameObject.Find(AvatarName).GetComponent<Vrm10Instance>(); animator = avatar.GetComponent<Animator>();
        reference = ArdyLocomotionAvatarReference.FromImportedPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath).GetComponentInChildren<Vrm10Instance>(true), AvatarPath);
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/AIChatTookit/Animation/Animator Controller.controller");
        Require(animator.runtimeAnimatorController != null && !avatar.enabled, "Project Animator/direct Humanoid fixture required.");
        animator.enabled = true; animator.SetInteger("state", 0);
        var environment = avatar.gameObject.scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Collider>(true)).Single(c => c.name == "リビング床").transform.root;
        var host = new GameObject("ARDY low table validation host");
        navigation = host.AddComponent<ArdyRoomNavigation>();
        navigation.ExcludedVisualRoots = environment.GetComponentsInChildren<Transform>(true).Where(t => t.name == "水面").ToArray();
        navigation.AgentHeight = Mathf.Max(1.1f, (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * avatar.transform.lossyScale.y + .18f);
        Require(navigation.Build(environment, avatar.transform, out string reason), reason);
        report.sources = navigation.SourceCount; report.proxies = navigation.GeometryProxyCount;
        controller = host.AddComponent<ArdyRoomLocomotionController>(); controller.Configure(avatar, reference, navigation);
        controller.AdditionalStepGuard = (a, b) => ArdyRoomApproachPlanner.StepKeepsUserClearance(a, b, RecordedUser,
            navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance) ? null : "用户身体间距不足。";
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        previous = avatar.transform.position; started = Time.realtimeSinceStartup; phase = 0; lastFrame = -1;
    }
    private static void GeometryChecks()
    {
        Require(navigation.TryPlan(RecordedStart, RecordedGoal, out var route, out string reason), "Recorded op39 route still plans: " + reason);
        Vector3 offset = reference.Get(HumanBodyBones.Hips).positionInRoot * avatar.transform.lossyScale.y; offset.y = 0;
        foreach (float heading in new[] { 180f, 90f, 0f })
        {
            var item = new GeometryCase { heading = heading, desired = .65f, route = route.Corners, length = route.Length };
            report.geometry.Add(item);
            // A neutral stationary start for each heading: all are physically feasible to turn before walking.
            Vector3 source = Quaternion.Euler(0, heading, 0) * offset; source.y = .95f;
            Func<Vector3, Vector3, bool> guard = (a, b) => { item.callbacks++; a.y = b.y = route.GroundY; return navigation.ValidateStep(a, b, out _, out _); };
            try
            {
                var targets = ArdyRoomStreamTrajectory.Build(route, source, heading, 0, .65f, Quaternion.identity, RecordedStart, 1, 40, 0, offset, guard);
                Vector3 prior = RecordedStart;
                foreach (var target in targets)
                {
                    Vector3 next = RecordedStart + target.rootPosition - Quaternion.Euler(0, target.headingDegrees, 0) * offset;
                    next.y = route.GroundY;
                    Require(guard(prior, next), "Every control root passes unchanged NavMesh/support/body checks.");
                    Require(Vector3.Distance(prior, next) <= .081f, "No target jump.");
                    item.verifiedSamples++; prior = next;
                }
                item.firstWindowTravel = Horizontal(prior - RecordedStart).magnitude; item.outcome = "verified";
                Require(item.firstWindowTravel > .01f || Mathf.Abs(Mathf.DeltaAngle(heading, targets[39].headingDegrees)) > 30f,
                    "Stationary start must make translation or bounded facing progress within H40, not return idle-only targets.");
            }
            catch (Exception e) { item.outcome = "failed"; item.reason = e.Message; throw; }
        }
        Require(Horizontal(avatar.transform.position - RecordedStart).magnitude < .00001f, "Control planning does not move the actual avatar.");
        string replay = SessionState.GetString(Key + "RecordedSeams", "");
        if (!string.IsNullOrEmpty(replay))
            report.recordedSeamsReplayJson = ArdyRoomStreamTrajectoryRegression.ReplayRecordedSeams(replay,
                (a, b) => { navigation.TryPlan(a, b, out var planned, out _); return planned; },
                (a, b) => navigation.ValidateStep(a, b, out _, out _));
    }
    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue)) { Finish(new TimeoutException("Isolated low-table timeout.")); return; }
        if (!EditorApplication.isPlaying || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            float now = Time.realtimeSinceStartup;
            if (phase == 0)
            {
                if (now - started < 1f) return;
                GeometryChecks();
                if (report.geometryOnly) { Finish(null); return; }
                Require(navigation.TryPlan(Ground(), RecordedGoal, out var route, out string reason), reason);
                controller.BeginContinuousFollow(route, RecordedUser, RecordedUser, Vector3.zero, .65f, false);
                phase = 1; updateAt = now; return;
            }
            float step = Vector3.Distance(previous, avatar.transform.position); previous = avatar.transform.position;
            report.maximumRootStep = Mathf.Max(report.maximumRootStep, step); report.travel += step;
            report.everPlayed |= controller.Player.IsPlaying;
            Require(step < .12f && !avatar.enabled, "No unguarded root jump or unintended VRM owner.");
            Require(now - started < 55, "One fixed native operation did not reach its goal in 55 seconds.");
            if (phase == 2)
            {
                if (controller.Player.HasPoseOwnership) { Require(now - stoppedAt < 3, "Smooth stop ownership did not release."); return; }
                report.returned = true; Require(animator.enabled && report.everPlayed && report.travel > 1.5f, "Must actually traverse the room and return to Animator.");
                stoppedAt = now; phase = 3; return;
            }
            if (phase == 3)
            {
                Require(controller.IsContinuousFollowActive && controller.StreamSessionId == null && !controller.Player.HasPoseOwnership,
                    "Arrival must remain in follow waiting instead of starting another centimetre-sized motion.");
                if (now - stoppedAt < 1.1f) return;
                report.stayedIdle = true; Finish(null); return;
            }
            Require(controller.ExecutionState != ArdyRoomExecutionState.Blocked && controller.ExecutionState != ArdyRoomExecutionState.Failed, "Native stream failed: " + controller.Status);
            float distance = Horizontal(Ground() - RecordedGoal).magnitude;
            if (controller.Stage == "stream-waiting" && controller.StreamSessionId == null && report.everPlayed)
            {
                Require(distance <= controller.ArrivalTolerance, "Autonomous arrival must reach the real root tolerance.");
                report.autonomousArrival = true; stoppedAt = now; phase = 2; return;
            }
            if (now >= updateAt)
            {
                updateAt = now + .2f;
                Require(navigation.TryPlan(Ground(), RecordedGoal, out var route, out string reason), reason);
                float speed = Mathf.Min(.65f, Mathf.Sqrt(1.5f * Mathf.Max(0, route.Length - .04f)));
                Require(controller.TryUpdateContinuousFollow(route, RecordedUser, RecordedUser, Vector3.zero, speed, false, out reason), reason);
                report.samples.Add(new Sample { seconds = now - started, root = avatar.transform.position, rootStep = step, desired = speed, measured = controller.StreamMeasuredSpeed,
                    buffer = controller.StreamBufferSeconds, steps = controller.StreamStepCount, goalDistance = distance, state = controller.ExecutionState.ToString(), stage = controller.Stage });
            }
        }
        catch (Exception e) { Finish(e); }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        report = report ?? new Report(); report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        report.duration = Time.realtimeSinceStartup - started; report.sceneShaAfter = Hash(ScenePath);
        if (report.sceneShaBefore != report.sceneShaAfter) { report.status = "failed"; report.error += " Scene hash changed."; }
        if (controller != null)
        {
            report.finalState = controller.ExecutionState.ToString(); report.finalStatus = controller.Status; report.steps = controller.StreamStepCount; report.underruns = controller.StreamUnderrunCount;
            report.segments = controller.Segments.ToList();
            report.rejectedPrefixes = controller.ReadRecentRejectedPrefixes();
        }
        if (avatar != null) { report.actualRoot = avatar.transform.position; report.finalDistance = Horizontal(Ground() - RecordedGoal).magnitude;
            report.rootStillAdmitted = navigation.TryValidatePlanStart(Ground(), out _, out _, out _); }
        string output = SessionState.GetString(Key + "Output", "Logs/ardy-lowtable-stream"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "lowtable-stream.json"), JsonUtility.ToJson(report, true));
        if (error != null) Debug.LogException(error);
        controller?.Cancel(); navigation?.Clear();
        SessionState.SetInt(Key + "ExitCode", report.status == "passed" ? 0 : 1); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    { SessionState.SetBool(Key + "Active", false); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); EditorApplication.Exit(SessionState.GetInt(Key + "ExitCode", 1)); }
    private static Vector3 Ground() => avatar.transform.position + Vector3.up * reference.groundY * avatar.transform.lossyScale.y;
    private static Vector3 Horizontal(Vector3 p) { p.y = 0; return p; }
    private static void Require(bool value, string why) { if (report != null) report.checks++; if (!value) throw new InvalidOperationException(why); }
    private static string Argument(string key, string fallback) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
}
