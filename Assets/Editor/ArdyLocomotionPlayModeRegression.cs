using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Short actual Unity player-loop / VRM ControlRig integration, never a motion-quality benchmark.</summary>
[InitializeOnLoad]
public static class ArdyLocomotionPlayModeRegression
{
    private const string Key = "NeEEvA.ARDY.LocomotionPlayMode.";
    private const string ControllerPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, modelPath, modelSha256, clipPath, clipSha256, playerSha256;
        public string method = "Actual PlayMode LateUpdate -> VRM ControlRig -> actual Humanoid bones. Editor callback reads completed frames; no SampleAt, manual Tick, Animator.Update or VRM.Process calls.";
        public string scope = "One selected current imported VRM; two actual ControlRigs with root yaw applied before versus after rig construction; approximately 3 seconds of the provided clip, then release and actual Animator resume.";
        public string limitation = "Integration correctness only: not whole-route completion, naturalness, skin-foot contact, collision, follow-user, Qwen planning or latency acceptance. Predicted contact markers are not measured contacts.";
        public int checks, sampledFrames;
        public float sampledClipSeconds;
        public List<FixtureResult> fixtures = new List<FixtureResult>();
    }
    [Serializable] private sealed class FixtureResult
    {
        public string name;
        public bool actualControlRig, vrmLateUpdateEnabled, originalAnimatorRestored, disabledAnimatorRemainedDisabled, upperBodyPlayerRestored;
        public bool actualOnDisableReleasedOwnership;
        public float initialYaw, motionScale, maxRootPositionErrorMetres, maxRootYawErrorDegrees, maxHipsPositionErrorMetres;
        public float maxHipsRotationErrorDegrees, maxLegRotationErrorDegrees, maximumRootTravelMetres;
        public float actualLegExcursionDegrees, animatorTimeAdvanceAfterRelease;
        public int frames;
    }
    private sealed class Fixture
    {
        public Vrm10Instance vrm;
        public Animator animator, disabledAnimator;
        public ArdyMotionPlayer upper;
        public ArdyLocomotionPlayer player;
        public ArdyLocomotionAvatarReference reference;
        public FixtureResult result;
        public Vector3 startRoot, initialPelvisHorizontal, stopRoot;
        public Quaternion startYaw, alignment, firstLeg, stopYaw;
        public float worldScale, groundWorld, resumeAnimatorTime;
        public Transform Bone(HumanBodyBones bone) => vrm.Humanoid.GetBoneTransform(bone);
    }
    private static Report report;
    private static ArdyLocomotionClip clip;
    private static readonly List<Fixture> fixtures = new List<Fixture>();
    private static int phase, lastFrame;
    private static float phaseTime, sampleDuration;

    static ArdyLocomotionPlayModeRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try { Initialize(); }
                catch (Exception error) { Finish(error); }
            }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) FinalizeBatch();
        };
        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying)
                FinalizeBatch();
        };
    }

    /// <summary>Batch-only isolated project; pass -ardyLocomotionClip and -ardyLocomotionOutput, omit -quit.</summary>
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the disposable unity-naturalness-validation project with -batchmode.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("PlayMode is already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace an edited validation scene.");
            string input = Argument("-ardyLocomotionClip", "");
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("-ardyLocomotionClip is required.");
            input = Path.GetFullPath(input);
            ArdyLocomotionClip.Parse(File.ReadAllText(input));
            string asset = SelectAvatar();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            var imported = prefab.GetComponentInChildren<Vrm10Instance>(true);
            ArdyLocomotionAvatarReference.FromImportedPrefab(imported, asset).Validate();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null) throw new InvalidOperationException("The actual Animator controller is missing.");
            SessionState.SetString(Key + "Avatar", asset);
            SessionState.SetString(Key + "Clip", input);
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyLocomotionOutput", "Logs/ardy-locomotion-playmode")));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (int i = 0; i < 2; i++)
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                root.name = "ARDY-Locomotion-PlayMode-" + i;
                root.transform.SetPositionAndRotation(new Vector3(i * 5f, .25f, -1f), Quaternion.Euler(0, i == 0 ? 37 : 0, 0));
                root.transform.localScale = Vector3.one * (i == 0 ? .9f : 1.1f);
                var vrm = root.GetComponent<Vrm10Instance>();
                if (vrm == null || root.GetComponent<Animator>() == null)
                    throw new InvalidOperationException("Expected a root VRM and Animator on the selected imported avatar.");
                vrm.enabled = false;
                vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = false; animator.runtimeAnimatorController = null;
                    animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            }
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 90f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        string asset = SessionState.GetString(Key + "Avatar", "");
        string input = SessionState.GetString(Key + "Clip", "");
        report = new Report { unityVersion = Application.unityVersion, modelPath = asset, modelSha256 = Hash(asset),
            clipPath = input, clipSha256 = Hash(input), playerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.cs") };
        clip = ArdyLocomotionClip.Parse(File.ReadAllText(input));
        sampleDuration = Mathf.Min(3f, clip.LastSampleSeconds);
        Check(sampleDuration >= .5f, "Provide at least half a second of locomotion for real frame observation.");
        var imported = AssetDatabase.LoadAssetAtPath<GameObject>(asset).GetComponentInChildren<Vrm10Instance>(true);
        var importedReference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, asset);
        var runtimeFlag = typeof(Vrm10Instance).GetField("m_useControlRig", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(runtimeFlag != null, "Cannot set the actual VRM import-time ControlRig option.");
        fixtures.Clear();
        for (int i = 0; i < 2; i++)
        {
            var root = GameObject.Find("ARDY-Locomotion-PlayMode-" + i);
            Check(root != null, "Missing isolated PlayMode fixture.");
            var f = new Fixture { vrm = root.GetComponent<Vrm10Instance>(), animator = root.GetComponent<Animator>(), reference = importedReference,
                result = new FixtureResult { name = root.name } };
            Check(!f.vrm.enabled && !f.animator.enabled, "Rig must be constructed from imported rest, before Animator evaluation.");
            runtimeFlag.SetValue(f.vrm, true);
            var actualRig = f.vrm.Runtime.ControlRig;
            Check(actualRig != null, "VRM did not construct its real runtime ControlRig.");
            if (i == 1) root.transform.rotation = Quaternion.Euler(0, 79, 0);
            f.result.actualControlRig = true;
            f.vrm.enabled = true;
            f.result.vrmLateUpdateEnabled = f.vrm.isActiveAndEnabled && f.vrm.UpdateType == Vrm10Instance.UpdateTypes.LateUpdate;
            Check(f.result.vrmLateUpdateEnabled, "Real VRM LateUpdate must remain enabled.");
            f.animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            f.animator.enabled = true;
            f.animator.SetInteger("state", 0);
            var disabled = new GameObject("Originally disabled Animator"); disabled.transform.SetParent(root.transform, false);
            f.disabledAnimator = disabled.AddComponent<Animator>(); f.disabledAnimator.enabled = false;
            f.upper = root.AddComponent<ArdyMotionPlayer>();
            f.upper.Bind(f.vrm);
            f.player = root.AddComponent<ArdyLocomotionPlayer>();
            f.player.Bind(f.vrm, importedReference);
            f.startRoot = root.transform.position; f.startYaw = root.transform.rotation;
            f.worldScale = root.transform.lossyScale.y;
            f.alignment = f.startYaw * Quaternion.Inverse(Quaternion.Euler(0, clip.sourceHeadingDegrees, 0));
            Vector3 neutral = importedReference.Get(HumanBodyBones.Hips).positionInRoot;
            f.initialPelvisHorizontal = f.startRoot + f.startYaw * (new Vector3(neutral.x, 0, neutral.z) * f.worldScale);
            f.groundWorld = f.startRoot.y + importedReference.groundY * f.worldScale;
            f.result.initialYaw = f.startYaw.eulerAngles.y;
            f.result.motionScale = (neutral.y - importedReference.groundY) * f.worldScale / clip.sourceRootHeight;
            f.firstLeg = f.Bone(HumanBodyBones.LeftUpperLeg).rotation;
            f.player.Play(clip);
            Check(!f.animator.enabled && !f.upper.enabled && !f.disabledAnimator.enabled, "Locomotion failed to pause its actual body owners.");
            Check(f.vrm.enabled, "Locomotion disabled the actual VRM updater.");
            fixtures.Add(f); report.fixtures.Add(f.result);
        }
        phase = 0; lastFrame = Time.frameCount; phaseTime = Time.time;
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Actual locomotion PlayMode regression timed out.")); return; }
        if (!EditorApplication.isPlaying || EditorApplication.isPaused || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            if (phase == 0)
            {
                foreach (var f in fixtures) Observe(f);
                report.sampledFrames++;
                report.sampledClipSeconds = fixtures.Min(f => f.player.TimeSeconds);
                if (report.sampledClipSeconds < sampleDuration - .001f) return;
                Check(report.sampledFrames >= 5, "Too few completed actual Unity frames were observed.");
                foreach (var f in fixtures)
                {
                    f.stopRoot = f.vrm.transform.position; f.stopYaw = f.vrm.transform.rotation;
                    f.player.Stop();
                    f.result.originalAnimatorRestored = f.animator.enabled;
                    f.result.disabledAnimatorRemainedDisabled = !f.disabledAnimator.enabled;
                    f.result.upperBodyPlayerRestored = f.upper.enabled;
                    Check(f.animator.enabled && f.upper.enabled && !f.disabledAnimator.enabled && f.vrm.enabled,
                        "Stop did not restore the original enabled states.");
                    Check(Vector3.Distance(f.vrm.transform.position, f.stopRoot) < .0001f && Quaternion.Angle(f.vrm.transform.rotation, f.stopYaw) < .08f,
                        "Stop changed the reached root transform.");
                }
                phase = 1; phaseTime = Time.time;
            }
            else if (phase == 1)
            {
                if (Time.time - phaseTime < .15f) return;
                foreach (var f in fixtures) f.resumeAnimatorTime = f.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                phase = 2; phaseTime = Time.time;
            }
            else if (phase == 2)
            {
                if (Time.time - phaseTime < .4f) return;
                foreach (var f in fixtures)
                {
                    f.result.animatorTimeAdvanceAfterRelease = f.animator.GetCurrentAnimatorStateInfo(0).normalizedTime - f.resumeAnimatorTime;
                    Check(f.result.animatorTimeAdvanceAfterRelease > .005f, "The real Animator did not advance after ownership release.");
                    Check(!f.player.HasPoseOwnership && !f.player.IsPlaying && f.upper.enabled && f.vrm.enabled,
                        "A stopped locomotion player retained body ownership.");
                    Check(Vector3.Distance(f.vrm.transform.position, f.stopRoot) < .002f && Quaternion.Angle(f.vrm.transform.rotation, f.stopYaw) < .1f,
                        "The reached root transform drifted after handing control back.");
                    f.player.RestorePreview();
                    Check(Vector3.Distance(f.vrm.transform.position, f.startRoot) < .0001f && Quaternion.Angle(f.vrm.transform.rotation, f.startYaw) < .08f,
                        "RestorePreview failed to restore the explicit starting transform.");
                    f.player.Play(clip);
                }
                phase = 3; phaseTime = Time.time;
            }
            else
            {
                if (Time.time - phaseTime < .15f) return;
                foreach (var f in fixtures)
                {
                    Check(f.player.HasPoseOwnership && !f.animator.enabled && !f.upper.enabled, "Short replay did not acquire ownership before the actual OnDisable test.");
                    Vector3 before = f.vrm.transform.position;
                    Quaternion beforeYaw = f.vrm.transform.rotation;
                    f.player.enabled = false;
                    f.result.actualOnDisableReleasedOwnership = !f.player.HasPoseOwnership && !f.player.IsPlaying;
                    Check(f.result.actualOnDisableReleasedOwnership, "Actual PlayMode OnDisable failed to release locomotion ownership.");
                    Check(f.animator.enabled && f.upper.enabled && !f.disabledAnimator.enabled && f.vrm.enabled,
                        "Actual OnDisable did not restore the original body-owner states.");
                    Check(Vector3.Distance(f.vrm.transform.position, before) < .0001f && Quaternion.Angle(f.vrm.transform.rotation, beforeYaw) < .08f,
                        "Actual OnDisable changed the reached root position or yaw.");
                    f.player.RestorePreview();
                    Check(Vector3.Distance(f.vrm.transform.position, f.startRoot) < .0001f && Quaternion.Angle(f.vrm.transform.rotation, f.startYaw) < .08f,
                        "Disabled player could not restore its explicit preview starting transform.");
                }
                Finish(null);
            }
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Observe(Fixture f)
    {
        Check(f.vrm.enabled && f.vrm.Runtime.ControlRig != null && f.vrm.UpdateType == Vrm10Instance.UpdateTypes.LateUpdate,
            "Actual VRM LateUpdate/ControlRig ownership changed while playing.");
        Check(!f.animator.enabled && !f.upper.enabled && !f.disabledAnimator.enabled && f.player.HasPoseOwnership,
            "A paused body owner was re-enabled during locomotion.");
        float seconds = f.player.TimeSeconds;
        var src = clip.SampleRoot(seconds);
        Vector3 horizontal = src - clip.sourceOrigin; horizontal.y = 0;
        Vector3 expectedPelvis = f.initialPelvisHorizontal + f.alignment * (horizontal * f.result.motionScale);
        expectedPelvis.y = f.groundWorld + src.y * f.result.motionScale;
        Quaternion hipsDelta = clip.rotationClip.SampleDelta(0, seconds);
        Vector3 forward = hipsDelta * Vector3.forward; forward.y = 0;
        Quaternion expectedYaw = f.alignment * Quaternion.LookRotation(forward.normalized, Vector3.up);
        Vector3 neutral = f.reference.Get(HumanBodyBones.Hips).positionInRoot;
        Vector3 offset = expectedYaw * (new Vector3(neutral.x, 0, neutral.z) * f.worldScale);
        Vector3 expectedRoot = new Vector3(expectedPelvis.x - offset.x, f.startRoot.y, expectedPelvis.z - offset.z);
        f.result.maxRootPositionErrorMetres = Mathf.Max(f.result.maxRootPositionErrorMetres, Vector3.Distance(f.vrm.transform.position, expectedRoot));
        f.result.maxRootYawErrorDegrees = Mathf.Max(f.result.maxRootYawErrorDegrees, Quaternion.Angle(f.vrm.transform.rotation, expectedYaw));
        f.result.maxHipsPositionErrorMetres = Mathf.Max(f.result.maxHipsPositionErrorMetres, Vector3.Distance(f.Bone(HumanBodyBones.Hips).position, expectedPelvis));
        f.result.maxHipsRotationErrorDegrees = Mathf.Max(f.result.maxHipsRotationErrorDegrees,
            Quaternion.Angle(f.Bone(HumanBodyBones.Hips).rotation, f.alignment * hipsDelta * f.reference.Get(HumanBodyBones.Hips).rotationInRoot));
        foreach (var pair in new[] {
            (HumanBodyBones.LeftUpperLeg, "LeftUpLeg"), (HumanBodyBones.LeftLowerLeg, "LeftLeg"), (HumanBodyBones.LeftFoot, "LeftFoot"),
            (HumanBodyBones.RightUpperLeg, "RightUpLeg"), (HumanBodyBones.RightLowerLeg, "RightLeg"), (HumanBodyBones.RightFoot, "RightFoot") })
        {
            int source = Array.IndexOf(clip.rotationClip.jointNames, pair.Item2);
            Quaternion expected = f.alignment * clip.rotationClip.SampleDelta(source, seconds) * f.reference.Get(pair.Item1).rotationInRoot;
            f.result.maxLegRotationErrorDegrees = Mathf.Max(f.result.maxLegRotationErrorDegrees, Quaternion.Angle(f.Bone(pair.Item1).rotation, expected));
        }
        f.result.maximumRootTravelMetres = Mathf.Max(f.result.maximumRootTravelMetres, Vector3.Distance(f.startRoot, f.vrm.transform.position));
        f.result.actualLegExcursionDegrees = Mathf.Max(f.result.actualLegExcursionDegrees, Quaternion.Angle(f.firstLeg, f.Bone(HumanBodyBones.LeftUpperLeg).rotation));
        f.result.frames++;
        Check(f.result.maxRootPositionErrorMetres < .001f && f.result.maxRootYawErrorDegrees < .15f, "Root position or heading did not follow the actual source sample.");
        Check(f.result.maxHipsPositionErrorMetres < .002f && f.result.maxHipsRotationErrorDegrees < .3f,
            "VRM LateUpdate did not transfer pelvis position/rotation correctly to the actual Humanoid.");
        Check(f.result.maxLegRotationErrorDegrees < .4f, "Actual post-VRM leg rotations disagree with the source full-body sample.");
    }

    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        if (report == null) report = new Report { unityVersion = Application.unityVersion };
        report.status = error == null ? "passed" : "failed";
        report.error = error?.ToString();
        if (error != null) Debug.LogException(error);
        foreach (var f in fixtures) if (f.player != null) f.player.RestorePreview();
        string output = SessionState.GetString(Key + "Output", "Logs/ardy-locomotion-playmode");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "playmode-report.json"), JsonUtility.ToJson(report, true));
        SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1);
        SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        else EditorApplication.delayCall += FinalizeBatch;
    }

    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        int code = SessionState.GetInt(Key + "ExitCode", 1);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Debug.Log("[ArdyLocomotionPlayModeRegression] Exit " + code + "; report: " + SessionState.GetString(Key + "Output", "") + "/playmode-report.json");
        EditorApplication.Exit(code);
    }

    private static string SelectAvatar()
    {
        string requested = Argument("-ardyLocomotionAvatar", "");
        var paths = string.IsNullOrEmpty(requested) ? AssetDatabase.GetAllAssetPaths()
            .Where(p => p.StartsWith("Assets/Model/", StringComparison.Ordinal) && p.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p)
            : new[] { requested }.OrderBy(p => p);
        foreach (var path in paths)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null && prefab.GetComponent<Vrm10Instance>() != null && prefab.GetComponent<Animator>() != null) return path;
        }
        throw new InvalidOperationException("No usable current imported VRM was found.");
    }
    private static string Argument(string key, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(Path.GetFullPath(path)))
        using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static void Check(bool value, string message)
    {
        if (report != null) report.checks++;
        if (!value) throw new InvalidOperationException(message);
    }
}
