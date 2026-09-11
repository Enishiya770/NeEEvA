using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Actual post-VRM observation acceptance, plus deterministic temporal boundary fixtures.</summary>
[InitializeOnLoad]
public static class ArdyMotionObservationRegression
{
    private const string Key = "NeEEvA.ARDY.ObservationRegression.";
    private const string AnimatorAsset = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private static readonly string[] Models = { "NEVA", "NeEEvA", "NEVA", "NeEEvA" };
    private static readonly float[] Scales = { 1, .8f, 1.2f, 1 };
    private static string Output => Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/ardy-observation"));
    [Serializable] private sealed class SavedScene { public string path; public bool isLoaded, isActive; }
    [Serializable] private sealed class SavedScenes { public SavedScene[] scenes; }
    [Serializable] private sealed class Case
    {
        public string id, expectedStatus, clipOrigin;
        public ArdyMotionGoal goal;
        public ArdyMotionObservation result;
        public List<ArdyMotionPoseSample> samples = new List<ArdyMotionPoseSample>();
        public float maximumActualReadError, maximumControlRigPositionDifference;
    }
    [Serializable] private sealed class FixtureReport
    {
        public string model, modelSha256, branch;
        public float scale, yaw;
        public List<Case> cases = new List<Case>();
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, goalSha256, observationSha256, observerSha256, playerSha256, harnessSha256;
        public string execution = "Actual Animator -> Player LateUpdate10000 -> VRM LateUpdate -> Observer12100 -> independent test12500. No manual Player.Tick/Animator.Update/VRM.Process.";
        public string scope = "Four real imported models with scale/yaw variation and both actual Humanoid/ControlRig branches. Synthetic Core27 poses test measurement regions, not native generation success. The archived failed bunny response is replayed byte-derived without generation or pose correction.";
        public string limitations = "Necessary wrist regions only. Reached is historical sustained evidence, not full semantics/naturalness/fingers, nor a claim that the pose is currently held. No services, network, chat or original scene edits.";
        public string bunnyTrace, bunnySha256;
        public int checks, actualFrames;
        public List<string> aggregationCases = new List<string>();
        public List<FixtureReport> fixtures = new List<FixtureReport>();
    }
    private sealed class Fixture
    {
        public GameObject root;
        public Vrm10Instance vrm;
        public ArdyMotionPlayer player;
        public ArdyMotionObserver observer;
        public FixtureReport report;
        public Case current;
        public float lastStored = -100;
        public Transform Bone(HumanBodyBones id) => vrm.Humanoid.GetBoneTransform(id);
        public Fixture(int index)
        {
            root = GameObject.Find("ARDY-Observation-" + index);
            Check(root != null, "Missing isolated observation fixture");
            vrm = root.GetComponent<Vrm10Instance>();
            if (index >= 2)
            {
                typeof(Vrm10Instance).GetField("m_useControlRig", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(vrm, true);
                Check(vrm.Runtime.ControlRig != null, "Actual ControlRig was not created");
                Check(vrm.Humanoid.GetBoneTransform(HumanBodyBones.LeftHand) != vrm.Runtime.ControlRig.GetBoneTransform(HumanBodyBones.LeftHand),
                    "Actual and normalized control transforms were not distinct");
                if (index == 3) root.transform.rotation = Quaternion.Euler(0, 135, 0);
                vrm.enabled = true;
            }
            var animator = root.GetComponent<Animator>();
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorAsset);
            animator.enabled = true; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.SetInteger("state", 0); animator.Play("Base Layer.idle", 0, 0);
            player = root.AddComponent<ArdyMotionPlayer>(); player.Bind(vrm);
            observer = root.AddComponent<ArdyMotionObserver>(); observer.Bind(player);
            report = new FixtureReport { model = Models[index], scale = Scales[index], yaw = root.transform.eulerAngles.y,
                modelSha256 = Hash("Assets/Model/" + Models[index] + ".vrm"), branch = index >= 2 ? "actual-ControlRig-and-VRM" : "actual-Humanoid-VRM-disabled" };
            ArdyMotionObservationRegression.report.fixtures.Add(report);
        }
        public void Begin(string id, ArdyMotionClip clip, ArdyMotionGoal goal, string expected, string origin)
        {
            player.PlayStream(clip, ArdyMotionMask.UpperBody, true);
            Check(!player.MotionPlaybackCompletedNaturally && !player.MotionPlaybackWasInterrupted, "New playback retained prior end flags");
            observer.BeginObservation(id, goal);
            current = new Case { id = id, goal = goal?.Copy(), expectedStatus = expected, clipOrigin = origin };
            report.cases.Add(current); lastStored = -100;
        }
        public void Sample()
        {
            var snapshot = observer.Observation;
            if (snapshot?.actual == null || snapshot.actual.frame != Time.frameCount) return;
            Check(snapshot.actual.playbackSerial == player.MotionPlaybackSerial, "Captured a stale playback identity");
            var actual = snapshot.actual;
            var inverse = Quaternion.Inverse(root.transform.rotation);
            foreach (bool left in new[] { true, false })
            {
                var arm = left ? actual.left : actual.right;
                Check(arm.available, "Actual imported arm unavailable");
                var hand = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var upper = Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                var lower = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                float length = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
                Vector3 point = inverse * (hand.position - root.transform.position);
                float error = Vector3.Distance(point, arm.wristCharacter);
                Check(error < .00001f && Mathf.Abs(length - arm.armLengthMeters) < .00001f, "Observer did not measure actual Humanoid world geometry");
                Check(Vector3.Distance((point - inverse * (upper.position - root.transform.position)) / length,
                    arm.wristFromShoulderNormalized) < .00002f, "Character axes/actual arm-scale normalization changed");
                current.maximumActualReadError = Mathf.Max(current.maximumActualReadError, error);
                if (report.branch.StartsWith("actual-ControlRig"))
                {
                    var controlled = vrm.Runtime.ControlRig.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    current.maximumControlRigPositionDifference = Mathf.Max(current.maximumControlRigPositionDifference,
                        Vector3.Distance(inverse * (controlled.position - root.transform.position), arm.wristCharacter));
                }
            }
            if (Time.time - lastStored >= .02f) { current.samples.Add(actual.Copy()); lastStored = Time.time; }
            // Returned snapshots must not allow callers to rewrite live or completed evidence.
            snapshot.actual.left.armLengthMeters = -99;
            Check(observer.Observation.actual.left.armLengthMeters > 0, "Observation copy leaked mutable nested arm state");
        }
        public void Finish(bool cancelled = false)
        {
            var before = observer.Observation?.actual?.Copy();
            current.result = observer.FinishObservation(current.id, cancelled ? "regression-cancelled" : "completed", cancelled);
            Check(current.result != null && current.result.status == current.expectedStatus,
                report.model + " " + current.id + ": " + current.result?.status + " expected " + current.expectedStatus + " (" + current.result?.reason + ")");
            if (!cancelled) Check(player.MotionPlaybackCompletedNaturally && !player.MotionPlaybackWasInterrupted,
                "A complete buffered clip did not finish naturally");
            player.Stop();
            if (cancelled) Check(player.MotionPlaybackWasInterrupted && !player.MotionPlaybackCompletedNaturally, "Early external stop was marked natural");
            if (before != null) Check(observer.LastCompleted.actual.frame == before.frame, "Finish sampled a returned idle pose");
            Check(observer.FinishObservation("wrong-id", "stale") == null, "Stale identity altered current evidence");
        }
    }

    private static Report report;
    private static Fixture[] fixtures;
    private static ArdyMotionClip template, bunny;
    private static int stage, startedFrame;
    private static float stageStarted;
    static ArdyMotionObservationRegression()
    {
        EditorApplication.update += EditorUpdate;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) SessionState.SetString(Key + "Stage", "running");
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key + "Stage", "") == "exiting") EditorApplication.delayCall += FinalizeBatch;
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && !EditorApplication.isPlaying && SessionState.GetString(Key + "Stage", "") == "exiting") FinalizeBatch();
        };
    }
    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Only use this exiting entry point with -batchmode, without -quit.");
        try
        {
            Check(!EditorApplication.isPlayingOrWillChangePlaymode, "Already playing");
            for (int i = 0; i < SceneManager.sceneCount; i++) Check(!SceneManager.GetSceneAt(i).isDirty, "Unsaved scene: refusing replacement");
            SessionState.SetString(Key + "Scenes", JsonUtility.ToJson(new SavedScenes { scenes = EditorSceneManager.GetSceneManagerSetup()
                .Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray() }));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (int i = 0; i < 4; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/" + Models[i] + ".vrm");
                Check(prefab != null, "Missing real VRM prefab " + Models[i]);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = "ARDY-Observation-" + i;
                instance.transform.SetPositionAndRotation(new Vector3(i * 4, 0, 0), Quaternion.Euler(0, i == 3 ? 0 : 25 + i * 50, 0));
                instance.transform.localScale = Vector3.one * Scales[i];
                var vrm = instance.GetComponent<Vrm10Instance>(); vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
                var animator = instance.GetComponent<Animator>();
                animator.runtimeAnimatorController = i < 2 ? AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorAsset) : null;
                animator.enabled = i < 2; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            SessionState.SetBool(Key + "Active", true); SessionState.SetString(Key + "Stage", "entering");
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 120);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Finish(error); }
    }
    private static void EditorUpdate()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        if (SessionState.GetString(Key + "Stage", "") == "exiting") return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue)) { Finish(new TimeoutException("Observation test timed out")); return; }
        if (!EditorApplication.isPlaying || Time.time < .5f || report != null) return;
        try
        {
            report = new Report { unityVersion = Application.unityVersion,
                goalSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyMotionGoal.cs"),
                observationSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyMotionObservation.cs"),
                observerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyMotionObserver.cs"),
                playerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs"),
                harnessSha256 = Hash("Assets/Editor/ArdyMotionObservationRegression.cs") };
            template = ArdyMotionClip.Parse(Resources.Load<TextAsset>("ARDY/left-wave").text);
            string trace = Environment.GetEnvironmentVariable("ARDY_OBSERVATION_BUNNY_TRACE");
            if (string.IsNullOrEmpty(trace)) trace = Path.Combine(Application.dataPath, "../Server/ARDY/runtime/intent-alignment-review-20260910/traces/ardy-motion-20260910T112117383-3c0a518e0c284626bd76a41aede4e7c1.json");
            report.bunnyTrace = Path.GetFullPath(trace); report.bunnySha256 = Hash(trace);
            Check(report.bunnySha256 == "682a96dd9cb0b2ad09e1101c99ad555045b99064dcc8a7115bab2aa82cbe6d3f", "Bunny trace differs from archived exact response23");
            var chunks = JObject.Parse(File.ReadAllText(trace))["entries"].Where(e => (string)e["kind"] == "response")
                .Select(e => JObject.Parse((string)e["json"])).OrderBy(e => (int)e["chunkIndex"]).ToArray();
            Check(chunks.Length == 3, "Archived response needs all three windows");
            bunny = ArdyMotionClip.Parse(chunks[0]["clip"].ToString());
            bunny.frames = chunks.SelectMany(e => ArdyMotionClip.Parse(e["clip"].ToString()).frames).ToArray();
            bunny.id = "exact-response23-bunny-three-windows"; bunny.Validate(); Check(bunny.frames.Length == 120, "Bunny frames changed");
            fixtures = Enumerable.Range(0, 4).Select(i => new Fixture(i)).ToArray();
            Check(!fixtures[0].player.TryCaptureActualRenderedMotion(out _), "Idle is not a generated frame");
            AggregationFixtures(fixtures[0]);
            foreach (var fixture in fixtures) fixture.observer.Bind(fixture.player);
            new GameObject("ARDY-Actual-Observation-Test").AddComponent<ArdyMotionObservationRegressionSampler>();
            stage = 0; BeginStage();
        }
        catch (Exception error) { Finish(error); }
    }
    public static void SampleActualLateUpdate()
    {
        if (report == null || fixtures == null || SessionState.GetString(Key + "Stage", "") == "exiting") return;
        try
        {
            report.actualFrames++;
            foreach (var fixture in fixtures) fixture.Sample();
            float elapsed = Time.time - stageStarted;
            float duration = stage == 5 ? bunny.Duration + .4f : stage == 7 ? .10f : 1.45f;
            if (Time.frameCount == startedFrame || elapsed < duration) return;
            foreach (var fixture in fixtures)
            {
                fixture.Finish(stage == 7);
                if (stage != 7) Check(fixture.current.result.sampleCount >= 5, "Too few actual post-VRM samples");
            }
            stage++;
            if (stage > 8)
            {
                Finish(null); return;
            }
            BeginStage();
        }
        catch (Exception error) { Finish(error); }
    }
    private static void BeginStage()
    {
        stageStarted = Time.time; startedFrame = Time.frameCount;
        string[] names = { "outward", "forward", "above-head", "down", "forward-goal-with-outward-source", "exact-native-bunny", "no-goal", "cancelled-before-duration", "replaced-playback" };
        string region = stage == 4 ? "forward" : stage == 5 ? "near-head" : stage == 6 ? "any" : stage >= 7 ? "forward" : names[stage];
        var goal = new ArdyMotionGoal { leftGoal = region, rightGoal = region };
        string expected = stage == 4 || stage == 5 ? "not-reached" : stage == 6 ? "unassessed" : stage == 7 ? "unknown" : "reached";
        var clip = stage == 5 ? bunny : Synthetic(stage == 4 ? "outward" : stage >= 6 ? "forward" : region);
        foreach (var f in fixtures)
        {
            if (stage == 8)
            {
                f.player.PlayStream(Synthetic("down"), ArdyMotionMask.UpperBody, true);
                f.observer.BeginObservation("old-playback", goal);
                f.player.PlayStream(clip, ArdyMotionMask.UpperBody, true);
                // Replacement identity is rejected by the observer's real LateUpdate; observe it
                // in a separate deterministic lifecycle check before beginning the new playback.
                typeof(ArdyMotionObserver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.observer, null);
                Check(f.observer.LastCompleted.cancelled && f.observer.LastCompleted.status == "unknown", "Replaced playback reused old evidence");
            }
            f.Begin(names[stage], clip, goal, expected, stage == 5 ? "archived exact native return; synthetic replay on actual avatar" : "synthetic canonical fixture; no native generation claim");
        }
    }
    private static ArdyMotionClip Synthetic(string direction)
    {
        var clip = ArdyMotionClip.Parse(JsonUtility.ToJson(template));
        clip.id = "observation-synthetic-" + direction; clip.text = "Analytical measurement fixture";
        clip.source.rotationApplication = null;
        clip.frames = Enumerable.Range(0, 20).Select(_ => new ArdyMotionFrame { globalRotations = (Quaternion[])clip.restGlobalRotations.Clone() }).ToArray();
        foreach (var frame in clip.frames)
        {
            Quaternion left = Quaternion.identity, right = Quaternion.identity;
            if (direction == "forward") { left = Quaternion.Euler(0, 90, 0); right = Quaternion.Euler(0, -90, 0); }
            if (direction == "above-head") { left = Quaternion.Euler(0, 0, -90); right = Quaternion.Euler(0, 0, 90); }
            if (direction == "down") { left = Quaternion.Euler(0, 0, 90); right = Quaternion.Euler(0, 0, -90); }
            foreach (string name in new[] { "LeftArm", "LeftForeArm", "LeftHand" }) frame.globalRotations[Array.IndexOf(clip.jointNames, name)] = left;
            foreach (string name in new[] { "RightArm", "RightForeArm", "RightHand" }) frame.globalRotations[Array.IndexOf(clip.jointNames, name)] = right;
        }
        clip.Validate(); return clip;
    }
    private static void AggregationFixtures(Fixture f)
    {
        var goal = new ArdyMotionGoal { leftGoal = "near-head", rightGoal = "near-head" };
        void Feed(double time, bool left, bool right, bool valid = true)
        {
            var sample = new ArdyMotionPoseSample { available = true, headAvailable = valid, timeSeconds = time, frame = (int)(time * 1000),
                left = Arm(left, true), right = Arm(right, false) };
            typeof(ArdyMotionObserver).GetMethod("RecordActualSample", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.observer, new object[] { sample });
        }
        ArdyObservedArm Arm(bool near, bool left) => new ArdyObservedArm { available = true, armLengthMeters = .5f, extensionRatio = .8f,
            wristFromShoulderNormalized = Vector3.forward, wristFromHeadNormalized = near ? new Vector3(left ? -.2f : .2f, .1f, .1f) : Vector3.down,
            wristHeadDistanceNormalized = near ? Mathf.Sqrt(.06f) : 1 };
        void Start(string id) => f.observer.BeginObservation(id, goal);
        void End(string id, string expected, bool cancelled = false)
        {
            var result = f.observer.FinishObservation(id, "deterministic-temporal-fixture", cancelled);
            Check(result.status == expected, "Temporal fixture " + id + ": " + result.status + " expected " + expected);
            report.aggregationCases.Add(id + ": " + result.status + "; maxStable=" + result.maximumStableSeconds + "; gaps=" + result.observationGapCount);
        }
        Start("bilateral-non-overlap"); for (int i = 0; i <= 8; i++) Feed(i * .05, i % 2 == 0, i % 2 != 0); End("bilateral-non-overlap", "not-reached");
        Start("single-instant"); for (int i = 0; i <= 8; i++) Feed(i * .05, i == 4, i == 4); End("single-instant", "not-reached");
        Start("sustained-bilateral"); for (int i = 0; i <= 6; i++) Feed(i * .05, true, true); End("sustained-bilateral", "reached");
        Start("pause-gap"); Feed(0, true, true); Feed(.05, true, true); Feed(.4, true, true); Feed(.45, true, true); End("pause-gap", "unknown");
        Start("missing-head"); for (int i = 0; i <= 6; i++) Feed(i * .05, true, true, false); End("missing-head", "unknown");
        Start("cancelled-before-reach"); Feed(0, true, true); Feed(.05, true, true); End("cancelled-before-reach", "unknown", true);
        Start("cancelled-after-reach"); for (int i = 0; i <= 6; i++) Feed(i * .05, true, true); End("cancelled-after-reach", "reached", true);
        Start("historical-reach-not-current-hold"); for (int i = 0; i <= 6; i++) Feed(i * .05, true, true); Feed(.35, false, false); End("historical-reach-not-current-hold", "reached");
        Check(!f.observer.LastCompleted.currentlyWithinGoal, "Historical reach falsely implies current hold");
        var copy = f.observer.LastCompleted; copy.goal.leftGoal = "any"; copy.actual.left.available = false;
        Check(f.observer.LastCompleted.goal.leftGoal == "near-head" && f.observer.LastCompleted.actual.left.available, "Completed observation deep copy leaked");
        Start("before-first-sample"); End("before-first-sample", "unknown");
        Check(f.observer.CompletedObservations.Length == 8, "Completed observation history is not bounded8");
        f.observer.Bind(f.player); Check(f.observer.LastCompleted == null && f.observer.Observation == null, "Rebind retained other-avatar evidence");
        var invalid = new ArdyMotionPoseSample { available = true, headAvailable = true, left = Arm(true, true), right = Arm(true, false) };
        invalid.left.armLengthMeters = float.NaN;
        Check(!ArdyMotionGoalMeasurement.Evaluate(invalid, goal, out bool known) && !known, "Non-finite geometry was assessed as known");
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetString(Key + "Stage", "") == "exiting") return;
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        if (error != null) Debug.LogException(error);
        Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "observation-regression.json"), JsonUtility.ToJson(report, true));
        SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1); SessionState.SetString(Key + "Stage", "exiting");
        SessionState.SetBool(Key + "Active", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false); int code = SessionState.GetInt(Key + "ExitCode", 1);
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var saved = JsonUtility.FromJson<SavedScenes>(SessionState.GetString(Key + "Scenes", "{}"));
            if (saved?.scenes != null && saved.scenes.Length > 0 && saved.scenes.All(s => !string.IsNullOrEmpty(s.path)))
                EditorSceneManager.RestoreSceneManagerSetup(saved.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray());
        }
        catch (Exception error) { code = 1; Debug.LogException(error); }
        Debug.Log("[ArdyMotionObservationRegression] Exit " + code + "; report " + Output); EditorApplication.Exit(code);
    }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void Check(bool condition, string message) { if (report != null) report.checks++; if (!condition) throw new InvalidOperationException(message); }
}

[DefaultExecutionOrder(12500)]
public sealed class ArdyMotionObservationRegressionSampler : MonoBehaviour
{
    private void LateUpdate() => ArdyMotionObservationRegression.SampleActualLateUpdate();
}
