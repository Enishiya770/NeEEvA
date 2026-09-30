using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Actual imported head/eye rig, scheduled Play updates, temporary unsaved isolated scene.</summary>
[InitializeOnLoad]
public static class NevaSceneAttentionRegression
{
    private const string Key = "NeEEvA.SceneAttentionRegression.";
    private const string Actor = "Scene attention isolated VRM";
    [Serializable] private sealed class Report
    {
        public string status = "running", error;
        public string scope = "Actual imported NeEEvA VRM head/eyes and calibrated camera pose in isolated Play. Checks smooth turning/return, target ownership, native Animator idle coexistence, actual cached ARDY nod ownership, refusal of excessive angles and movement. No network, model decision or pixel rendering is implied.";
        public int checks;
        public float firstSettledErrorDegrees, animatorSettledErrorDegrees, maximumFrameHeadStepDegrees;
        public bool smoothEntryObserved, smoothReturnObserved, animatorAdvanced, headPoseUnchangedAfterArdyYield;
        public float directedViewChangeDegrees;
        public bool retainedViewObserved, relativeViewAccepted;
        public bool persistentHeldBeyondTwentySeconds, persistentYieldedToBody;
    }
    private sealed class Saved
    {
        public Transform transform;
        public Quaternion rotation;
        public Vector3 position, scale;
    }
    private static Report report;
    private static NevaSceneAttention attention;
    private static NevaEyeTracking tracking;
    private static NevaSceneVisionCapture vision;
    private static Animator animator;
    private static Transform userGaze;
    private static ArdyMotionPlayer player;
    private static readonly List<Saved> saved = new List<Saved>();
    private static Quaternion neutralHead, previousHead;
    private static Vector3 rootPosition;
    private static Quaternion rootRotation;
    private static Vector3 firstPoint;
    private static float phaseAt, returnFromAngle, animatorStart, nextPersistentRenewal;
    private static int phase, lastFrame;

    static NevaSceneAttentionRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                InitializeFixture();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Scene attention checks only run in the existing isolated validation project.");
        SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-nevaSceneAttentionOutput", "Logs/neva-scene-attention")));
        try
        {
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 100);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e) { Finish(e); }
    }

    private static async void InitializeFixture()
    {
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NeEEvA.vrm");
            GameObject actor;
            if (prefab != null) actor = UnityEngine.Object.Instantiate(prefab);
            else
            {
                // The isolated DLL fixture has runtime VRM importers but no editor
                // asset importer. Load the exact source avatar in Play in that case.
                string avatarPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../../Assets/Model/NeEEvA.vrm"));
                if (!File.Exists(avatarPath)) throw new InvalidOperationException("The actual source NeEEvA VRM is missing.");
                var imported = await Vrm10.LoadPathAsync(avatarPath, controlRigGenerationOption: ControlRigGenerationOption.None, showMeshes: true);
                if (imported == null) throw new InvalidOperationException("The actual VRM runtime importer returned no avatar.");
                actor = imported.gameObject;
            }
            actor.name = Actor;
            actor.transform.SetPositionAndRotation(new Vector3(2, .3f, -1), Quaternion.Euler(0, 37, 0));
            foreach (var component in actor.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
            var vrm = actor.GetComponentInChildren<Vrm10Instance>(true);
            // Runtime import registers secondary hair physics in a global job
            // service even when the VRM component is disabled. This static-rig
            // isolation check disables that independent writer in its own avatar.
            vrm.Runtime.SpringBone.Dispose();
            var eyes = vrm.GetComponent<NevaEyeTracking>() ?? vrm.gameObject.AddComponent<NevaEyeTracking>();
            eyes.Configure(vrm); eyes.enabled = true;
            var source = vrm.GetComponent<NevaSceneVisionCapture>() ?? vrm.gameObject.AddComponent<NevaSceneVisionCapture>();
            source.Configure(vrm, eyes); source.enabled = true;
            var focus = vrm.GetComponent<NevaSceneAttention>() ?? vrm.gameObject.AddComponent<NevaSceneAttention>();
            focus.Configure(eyes); focus.enabled = true;
            var gaze = new GameObject("Scene attention unchanged user gaze").transform;
            gaze.position = eyes.EyeOrigin + eyes.HeadFrameRotation * Vector3.forward * 3f;
            eyes.gazeTarget = gaze;
            var idle = vrm.GetComponent<Animator>();
            idle.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/AIChatTookit/Animation/Animator Controller.controller");
            idle.applyRootMotion = false; idle.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // Runtime import and assigning a controller can finish initialization
            // at a player-loop boundary even with subsequent animation disabled.
            await System.Threading.Tasks.Task.Yield();
            await System.Threading.Tasks.Task.Yield();
            Initialize();
        }
        catch (Exception e) { Finish(e); }
    }

    private static void Initialize()
    {
        report = new Report(); saved.Clear(); phase = 0; lastFrame = -1;
        var actor = GameObject.Find(Actor);
        attention = actor.GetComponentInChildren<NevaSceneAttention>(true);
        tracking = actor.GetComponentInChildren<NevaEyeTracking>(true);
        vision = actor.GetComponentInChildren<NevaSceneVisionCapture>(true);
        animator = tracking.Target.GetComponent<Animator>(); userGaze = tracking.gazeTarget;
        Require(attention != null && tracking.IsInitialized && !tracking.Target.enabled, "Calibrated real rig, without competing full VRM updates.");
        Require(animator != null && animator.runtimeAnimatorController != null && !animator.enabled, "Static rig first; actual idle controller is available for later coexistence.");
        rootPosition = tracking.Target.transform.position; rootRotation = tracking.Target.transform.rotation;
        neutralHead = previousHead = tracking.Head.localRotation;
        foreach (var value in tracking.Target.GetComponentsInChildren<Transform>(true))
            saved.Add(new Saved { transform = value, position = value.localPosition, rotation = value.localRotation, scale = value.localScale });
        Require(!attention.TryLookAt("bad", new Vector3(float.NaN, 0, 0), out _), "Nonfinite target rejected.");
        Require(!attention.TryLookAt("behind", Point(150, 0), out _), "Target behind character does not force neck twist.");
        Require(!attention.TryLookAt("above", Point(0, 60), out _), "Excessive upward target rejected.");
        Require(!attention.TryLookAt("near", tracking.EyeOrigin, out _), "Coincident target rejected.");
        firstPoint = Point(32, 9);
        Require(attention.TryLookAt("first", firstPoint, out string reason), "First bounded target accepted: " + reason);
        Require(tracking.AttentionOwnerId == "first" && tracking.gazeTarget == userGaze, "Temporary eye target owns action without replacing configured user gaze.");
        phaseAt = Time.realtimeSinceStartup;
    }

    private static Vector3 Point(float yaw, float pitch) => tracking.EyeOrigin + tracking.HeadFrameRotation * Quaternion.Euler(-pitch, yaw, 0) * Vector3.forward * 3f;

    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("Attention Play regression timed out.");
            if (!EditorApplication.isPlaying || attention == null || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            float elapsed = Time.realtimeSinceStartup - phaseAt;
            if (elapsed > (phase == 24 ? 24f : 7f)) throw new TimeoutException("Attention phase " + phase + " did not complete: " + attention.State + " / " + attention.Status);
            float angle = Quaternion.Angle(neutralHead, tracking.Head.localRotation);
            if (phase <= 2)
            {
                report.maximumFrameHeadStepDegrees = Mathf.Max(report.maximumFrameHeadStepDegrees, Quaternion.Angle(previousHead, tracking.Head.localRotation));
                previousHead = tracking.Head.localRotation;
            }
            if (phase == 0)
            {
                if (angle > .3f && angle < 25f) report.smoothEntryObserved = true;
                if (!attention.IsSettled) return;
                report.firstSettledErrorDegrees = attention.AngularErrorDegrees;
                Require(report.smoothEntryObserved && angle > 25f, "Real head approaches first point through intermediate poses.");
                Require(attention.AngularErrorDegrees <= 2.1f, "Actual head points at requested point.");
                Require(vision.TryGetCurrentCameraPose(out Pose pose) && Quaternion.Angle(pose.rotation, tracking.HeadFrameRotation) < .01f &&
                    Vector3.Angle(pose.rotation * Vector3.forward, firstPoint - pose.position) < 2.1f, "Scene capture pose follows actual turned calibrated head.");
                CheckUntouched();
                Require(!attention.Release("obsolete"), "Unrelated cancellation cannot release first request.");
                // Point is expressed relative to original root so the replacement crosses the neutral pose.
                var second = tracking.EyeOrigin + rootRotation * Quaternion.Euler(6, -25, 0) * Vector3.forward * 3f;
                Require(attention.TryLookAt("second", second, out string reason), "Replacement accepted: " + reason);
                Require(!attention.Release("first") && attention.OwnerId == "second", "Late first completion cannot release replacement.");
                phase = 1; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 1 && attention.IsSettled)
            {
                Require(attention.AngularErrorDegrees <= 2.1f && tracking.AttentionOwnerId == "second", "Replacement settles with matching eye ownership.");
                CheckUntouched(); returnFromAngle = angle;
                Quaternion before = tracking.Head.localRotation;
                Require(attention.Release("second", "regression-return"), "Matching action releases attention.");
                Require(attention.IsReturning && Quaternion.Angle(before, tracking.Head.localRotation) < .01f, "Release starts smooth return without immediate head snap.");
                Require(tracking.AttentionOwnerId == null && tracking.gazeTarget == userGaze, "Normal user gaze resumes without changing authoring.");
                phase = 2; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 2)
            {
                if (angle > .3f && angle < returnFromAngle - .3f) report.smoothReturnObserved = true;
                if (attention.IsReturning) return;
                Require(report.smoothReturnObserved && angle < .1f && attention.State == "idle", "Head smoothly returns to exact underlying static pose.");
                CheckUntouched();
                Require(!attention.TryOrientView("too-large", 55, 0, out _) && !attention.TryOrientView("nan", float.NaN, 0, out _), "Unknown-view angle protocol is bounded and finite.");
                Require(attention.TryOrientView("explore-right-up", 28, 12, out string reason), "Real directional look requires no pre-existing target: " + reason);
                phase = 20; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 20 && attention.IsSettled)
            {
                report.directedViewChangeDegrees = Quaternion.Angle(rootRotation, tracking.HeadFrameRotation);
                Require(report.directedViewChangeDegrees > 20 && attention.AngularErrorDegrees <= 2.1f, "Directional exploration changed actual calibrated view.");
                Require(attention.RetainView("explore-right-up", 20) && !attention.RetainView("obsolete", 30), "Only current view owner may retain the observation view.");
                phase = 21; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 21 && elapsed > .5f)
            {
                report.retainedViewObserved = attention.IsSettled && attention.OwnerId == "explore-right-up" && Quaternion.Angle(rootRotation, tracking.HeadFrameRotation) > 20;
                Require(report.retainedViewObserved, "Changed view remains physically held for the following decision.");
                Require(attention.RetainPersistentView("explore-right-up") && !attention.RetainPersistentView("obsolete"),
                    "Persistent retention requires the settled current owner and rejects a late old verifier.");
                phase = 24; phaseAt = Time.realtimeSinceStartup; nextPersistentRenewal = phaseAt + 2f;
            }
            else if (phase == 24)
            {
                Require(attention.IsPersistent && attention.IsSettled && attention.OwnerId == "explore-right-up",
                    "Persistent stationary pose remains physically held between bounded verification leases.");
                if (Time.realtimeSinceStartup >= nextPersistentRenewal)
                {
                    Require(attention.RetainPersistentView("explore-right-up"), "Fresh-verifier lease can renew without restarting or snapping head.");
                    nextPersistentRenewal = Time.realtimeSinceStartup + 2f;
                }
                if (elapsed < 21f) return;
                report.persistentHeldBeyondTwentySeconds = attention.IsSettled && attention.IsPersistent && Quaternion.Angle(rootRotation, tracking.HeadFrameRotation) > 20;
                Require(report.persistentHeldBeyondTwentySeconds, "Actual persistent head pose survives the former twenty-second expiry.");
                report.relativeViewAccepted = attention.TryOrientView("explore-relative-left", -15, 0, out string reason);
                Require(report.relativeViewAccepted && !attention.IsPersistent && !attention.Release("explore-right-up"), "A relative new exploration replaces its exact predecessor, without stale cancellation: " + reason);
                phase = 22; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 22 && attention.IsSettled)
            {
                Require(attention.AngularErrorDegrees <= 2.1f && attention.CurrentHeadAngles.x > 8f && attention.CurrentHeadAngles.x < 18f,
                    "Second yaw is relative to actual held view, not a reset-to-neutral angle.");
                CheckUntouched(); attention.Cancel("explicit-stop-exploration");
                Require(attention.IsReturning && tracking.AttentionOwnerId == null, "Explicit stop releases retained exploration through smooth return.");
                phase = 23; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 23 && !attention.IsReturning)
            {
                CheckUntouched(); animator.enabled = true; animator.SetInteger("state", 0); animator.Play("Base Layer.idle", 0, 0);
                phase = 3; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 3 && elapsed > .3f)
            {
                animatorStart = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                Require(attention.TryLookAt("animated", Point(25, 5), out string reason), "Attention over actual Animator baseline accepted: " + reason);
                phase = 4; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 4 && attention.IsSettled)
            {
                report.animatorAdvanced = animator.GetCurrentAnimatorStateInfo(0).normalizedTime > animatorStart;
                report.animatorSettledErrorDegrees = attention.AngularErrorDegrees;
                Require(report.animatorAdvanced && attention.AngularErrorDegrees <= 2.1f, "Idle keeps advancing while actual head settles; no accumulated offsets.");
                Require(attention.RetainPersistentView("animated"), "Persistent pose may coexist with an advancing normal idle baseline.");
                player = tracking.Target.gameObject.AddComponent<ArdyMotionPlayer>(); player.Bind(tracking.Target);
                var nod = Resources.Load<TextAsset>("ARDY/nod"); Require(nod != null, "Actual cached ARDY nod exists.");
                player.Play(nod, ArdyMotionMask.Head); player.Tick(.1f);
                Quaternion ardyPose = tracking.Head.localRotation;
                typeof(NevaSceneAttention).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(attention, null);
                report.headPoseUnchangedAfterArdyYield = Quaternion.Angle(ardyPose, tracking.Head.localRotation) < .01f;
                report.persistentYieldedToBody = !attention.IsPersistent;
                Require(attention.State == "yielded" && tracking.AttentionOwnerId == null && report.headPoseUnchangedAfterArdyYield && report.persistentYieldedToBody,
                    "Incoming actual ARDY head motion owns its applied pose; attention yields without overwriting it.");
                Require(!attention.TryLookAt("blocked", Point(10, 0), out _), "New attention cannot acquire during actual ARDY playback.");
                player.enabled = false;
                phase = 5; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 5 && elapsed > .1f)
            {
                Require(attention.TryLookAt("move-conflict", Point(15, 0), out string reason), "New idle attention can acquire after gesture owner release: " + reason);
                phase = 6; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 6 && attention.IsSettled)
            {
                var changedRoot = tracking.Target.transform.position + Vector3.right * .05f;
                tracking.Target.transform.position = changedRoot;
                typeof(NevaSceneAttention).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(attention, null);
                Require(attention.State == "yielded" && tracking.Target.transform.position == changedRoot && tracking.AttentionOwnerId == null,
                    "External root movement yields attention without moving root back.");
                Require(attention.TryLookAt("disable", Point(10, 0), out string reason), "Acquire new request after movement settled: " + reason);
                attention.enabled = false;
                Require(!attention.IsBusy && tracking.AttentionOwnerId == null, "Disable clears its own head and eye ownership.");
                Require(tracking.gazeTarget == userGaze && Quaternion.Angle(tracking.Target.transform.rotation, rootRotation) < .01f,
                    "No user gaze retarget or body rotation introduced.");
                Finish(null);
            }
        }
        catch (Exception e) { Finish(e is TargetInvocationException && e.InnerException != null ? e.InnerException : e); }
    }

    private static void CheckUntouched()
    {
        bool valid = true;
        var changed = new List<string>();
        foreach (var value in saved)
        {
            var bone = value.transform;
            if (bone == tracking.Head || bone == tracking.LeftEye || bone == tracking.RightEye) continue;
            bool unchanged = Vector3.Distance(bone.localPosition, value.position) < .00001f && Quaternion.Angle(bone.localRotation, value.rotation) < .05f &&
                Vector3.Distance(bone.localScale, value.scale) < .00001f;
            valid &= unchanged;
            if (!unchanged) changed.Add(bone.name + " position=" + Vector3.Distance(bone.localPosition, value.position) + " angle=" + Quaternion.Angle(bone.localRotation, value.rotation));
        }
        Require(valid, "All non-head/eye local transforms unchanged during static attention. " + string.Join("; ", changed));
        Require(Vector3.Distance(tracking.Target.transform.position, rootPosition) < .00001f && Quaternion.Angle(tracking.Target.transform.rotation, rootRotation) < .01f,
            "Attention never translates or rotates character root.");
    }
    private static void Require(bool value, string message)
    { if (report != null) report.checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        if (report == null) report = new Report(); report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        string output = SessionState.GetString(Key + "Output", "Logs/neva-scene-attention"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "attention-regression.json"), JsonUtility.ToJson(report, true));
        SessionState.SetBool(Key + "Finished", true); SessionState.SetInt(Key + "Exit", error == null ? 0 : 1);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    private static void Exit() { SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(SessionState.GetInt(Key + "Exit", 1)); }
    private static string Argument(string key, string fallback)
    { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1]; return fallback; }
}
