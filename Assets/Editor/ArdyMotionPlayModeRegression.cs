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
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Real player-loop integration checks. No manual motion, Animator or VRM Process ticks.</summary>
[InitializeOnLoad]
public static class ArdyMotionPlayModeRegression
{
    private const string Key = "NeEEvA.ARDY.PlayModeRegression.";
    private const string AvatarAsset = "Assets/Model/NEVA.vrm";
    private const string ControllerAsset = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/ardy-motion-preview"));
    private static readonly HumanBodyBones[] ComparedBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightShoulder,
        HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
    };

    [Serializable] private sealed class SavedScene { public string path; public bool isLoaded; public bool isActive; }
    [Serializable] private sealed class SavedScenes { public SavedScene[] scenes; }
    [Serializable] private sealed class Report
    {
        public string status = "running";
        public string sourceAvatar = AvatarAsset;
        public string animatorController = ControllerAsset;
        public string updateMethod = "Actual Unity PlayMode Update / Animator evaluation / LateUpdate; sampled once per Time.frameCount by EditorApplication.update. No manual Tick, Animator.Update or Vrm.Runtime.Process.";
        public string humanoidConfiguration = "VRM disabled, Animator enabled from scene start, matching the private scene. Bind waits at least 0.5 seconds for a real idle pose. A synchronized Animator-only control provides the expected pose after stopping; full-blend arm rotations relative to each animated chest also match a known-rest ControlRig.";
        public string controlRigConfiguration = "Two real Vrm10Runtime ControlRigs created by setting the import option before first Runtime access; VRM LateUpdate enabled. Root yaw 65 degrees applied before versus after construction.";
        public string expressionMethod = "Synthetic aa viseme sinusoid injected into Audio2LipScript.Frame by reflection; the real Audio2LipScript.Update writes the mouth and the real BlinkController.Update/coroutine drives blinking. AudioSource is disabled and never plays.";
        public string limitation = "Isolated imported avatar fixtures; no chat, network, live ARDY service, TTS, audio recognition/synchronization, or scene lighting. Other blendshape channels retain nonzero sentinel weights.";
        public int checks;
        public int sampledFrames;
        public float elapsedSeconds;
        public float humanoidWristRiseMetres;
        public float humanoidActionSeparationMetres;
        public float animatorNormalisedTimeAdvance;
        public float returnToAnimatorMaxErrorDegrees;
        public float controlRigNeutralMaxErrorDegrees;
        public float controlRigYawMaxErrorDegrees;
        public float controlRigWristAboveShoulderMetres;
        public float lateHumanoidBindSeconds;
        public float lateHumanoidBindWristBelowShoulderMetres;
        public float humanoidToControlRigMaxErrorDegrees;
        public string expressionMesh;
        public string[] expressionBlendShapes;
        public int syntheticVisemeFramesVerified;
        public float mouthExpectedWeightMaxError;
        public float wavingMouthMinimum = 100f;
        public float wavingMouthMaximum;
        public float wavingBlinkMinimum = 100f;
        public float wavingBlinkMaximum;
        public string idlePngSha256;
        public string wavePngSha256;
        public string stoppedPngSha256;
    }

    private sealed class Fixture
    {
        public GameObject Root;
        public Vrm10Instance Vrm;
        public Animator Animator;
        public ArdyMotionPlayer Player;
        public Vector3 StartPosition;
        public Quaternion StartRotation;
        public Dictionary<HumanBodyBones, Quaternion> Neutral = new Dictionary<HumanBodyBones, Quaternion>();
        public Dictionary<SkinnedMeshRenderer, float[]> Expressions = new Dictionary<SkinnedMeshRenderer, float[]>();
        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);
        public Quaternion RelativeRotation(HumanBodyBones bone) => Quaternion.Inverse(Root.transform.rotation) * Bone(bone).rotation;
        public Quaternion ChestRelativeRotation(HumanBodyBones bone)
        {
            var chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest) ?? Bone(HumanBodyBones.Spine);
            return Quaternion.Inverse(chest.rotation) * Bone(bone).rotation;
        }

        public Fixture(string name, bool rig, bool yawAfterRig, bool withPlayer)
        {
            Root = GameObject.Find(name);
            Check(Root != null, "Missing temporary fixture " + name);
            Vrm = Root.GetComponent<Vrm10Instance>();
            Animator = Root.GetComponent<Animator>();
            Check(Vrm != null && Animator != null, "Missing VRM/Animator on fixture " + name);
            // Rig fixtures retain the imported T-pose; ordinary fixtures have already run their real Animator.
            if (rig)
            {
                typeof(Vrm10Instance).GetField("m_useControlRig", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(Vrm, true);
                Check(Vrm.Runtime.ControlRig != null, "Original VRM runtime did not construct a ControlRig");
                if (yawAfterRig) Root.transform.rotation = Quaternion.Euler(0f, 65f, 0f);
                Vrm.enabled = true;
            }
            else
            {
                Check(Vrm.Runtime.ControlRig == null, "Expected ordinary Humanoid branch");
                Vrm.enabled = false;
                float lowered = Bone(HumanBodyBones.LeftUpperArm).position.y - Bone(HumanBodyBones.LeftHand).position.y;
                Check(Time.time >= 0.5f && lowered > 0.15f, "Late Bind fixture did not reach the actual Animator idle before binding");
                if (withPlayer)
                {
                    report.lateHumanoidBindSeconds = Time.time;
                    report.lateHumanoidBindWristBelowShoulderMetres = lowered;
                }
            }
            StartPosition = Root.transform.position;
            StartRotation = Root.transform.rotation;
            foreach (var bone in ComparedBones)
                if (Bone(bone) != null) Neutral[bone] = RelativeRotation(bone);
            if (withPlayer)
            {
                Player = Root.AddComponent<ArdyMotionPlayer>();
                Player.Bind(Vrm);
                Check(Player.BoundBoneCount >= 12, "Too few bound bones in PlayMode");
            }
            foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                int count = renderer.sharedMesh.blendShapeCount;
                if (!rig && count > 0) renderer.SetBlendShapeWeight(0, 17f);
                Expressions[renderer] = Enumerable.Range(0, count).Select(renderer.GetBlendShapeWeight).ToArray();
                renderer.updateWhenOffscreen = true;
            }
            if (!rig)
            {
                Animator.enabled = true;
                Animator.SetInteger("state", 0);
                Animator.Play("Base Layer.idle", 0, 0f);
            }
        }
    }

    private static Report report;
    private static Fixture humanoid, control, rigBefore, rigAfter;
    private static Camera camera;
    private static int phase, lastFrame;
    private static float startedAt, phaseStartedAt, wristStart, animationStart;
    private static TextAsset neutralClip;
    private static AudioSource silentAudioSource;
    private static Audio2LipScript lips;
    private static BlinkController blink;
    private static OVRLipSync.Frame visemeFrame;
    private static SkinnedMeshRenderer faceRenderer;
    private static HashSet<int> dynamicExpressionIndices;
    private static int mouthAIndex, blinkIndex, injectedAtFrame;
    private static float expectedMouthWeight;

    static ArdyMotionPlayModeRegression()
    {
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.delayCall += Resume;
    }

    /// <summary>Use -batchmode -executeMethod ArdyMotionPlayModeRegression.RunBatch without -quit.</summary>
    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("This entry point exits Unity; use it only with -batchmode.");
        try
        {
            Check(!EditorApplication.isPlayingOrWillChangePlaymode, "PlayMode regression already running");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Check(!SceneManager.GetSceneAt(i).isDirty, "Refusing to replace a scene with unsaved edits");
            SessionState.SetString(Key + "Scenes", JsonUtility.ToJson(new SavedScenes
            {
                scenes = EditorSceneManager.GetSceneManagerSetup().Select(scene => new SavedScene
                    { path = scene.path, isLoaded = scene.isLoaded, isActive = scene.isActive }).ToArray()
            }));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarAsset);
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerAsset);
            Check(prefab != null && controller != null, "Missing avatar or actual Animator controller");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (int i = 0; i < 4; i++)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = "ARDY-PlayMode-" + i;
                instance.transform.SetPositionAndRotation(new Vector3(i * 4f, 0f, 0f), Quaternion.Euler(0f, i < 2 ? 25f : i == 2 ? 65f : 0f, 0f));
                foreach (var transform in instance.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 31;
                var vrm = instance.GetComponent<Vrm10Instance>();
                Check(vrm != null, "Imported avatar has no VRM instance");
                vrm.enabled = false;
                vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
                var animator = instance.GetComponent<Animator>();
                Check(animator != null, "Imported avatar has no Animator");
                animator.runtimeAnimatorController = i < 2 ? controller : null;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
                animator.enabled = i < 2;
            }
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetString(Key + "Stage", "entering");
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 90f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            SessionState.SetBool(Key + "Active", false);
            EditorApplication.Exit(1);
        }
    }

    private static void Resume()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        if (!EditorApplication.isPlaying && SessionState.GetString(Key + "Stage", "") == "exiting") FinalizeBatch();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode) SessionState.SetString(Key + "Stage", "ready");
        if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key + "Stage", "") == "exiting")
            EditorApplication.delayCall += FinalizeBatch;
    }

    private static void OnEditorUpdate()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        {
            Finish(new TimeoutException("Timed out entering/running the real PlayMode loop"));
            return;
        }
        string stage = SessionState.GetString(Key + "Stage", "");
        if (!EditorApplication.isPlaying || EditorApplication.isPaused || stage == "entering" || stage == "exiting") return;
        try
        {
            if (report == null)
            {
                if (Time.time >= 0.5f) Initialize();
                return;
            }
            if (Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;
            report.sampledFrames++;
            report.elapsedSeconds = Time.time - startedAt;
            Sample();
            if (SessionState.GetString(Key + "Stage", "") != "exiting") InjectNextViseme();
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Initialize()
    {
        Directory.CreateDirectory(Output);
        report = new Report();
        humanoid = new Fixture("ARDY-PlayMode-0", false, false, true);
        control = new Fixture("ARDY-PlayMode-1", false, false, false);
        rigBefore = new Fixture("ARDY-PlayMode-2", true, false, true);
        rigAfter = new Fixture("ARDY-PlayMode-3", true, true, true);
        InitializeExpressions();
        var source = ArdyMotionClip.Parse(Clip().text);
        foreach (var frame in source.frames) frame.globalRotations = (Quaternion[])source.restGlobalRotations.Clone();
        source.id = "regression-neutral-source-rest";
        neutralClip = new TextAsset(JsonUtility.ToJson(source));
        rigBefore.Player.Play(neutralClip, ArdyMotionMask.UpperBody);
        rigAfter.Player.Play(neutralClip, ArdyMotionMask.UpperBody);
        camera = CreateCamera(humanoid.Root);
        startedAt = phaseStartedAt = Time.time;
        lastFrame = Time.frameCount;
        phase = 0;
        InjectNextViseme();
        SessionState.SetString(Key + "Stage", "running");
    }

    private static void InitializeExpressions()
    {
        // These target names were read from NEVA.vrm's GLB mesh extras.targetNames and VRM blendShapeGroups.
        // Resolve actual imported indices again, so importer changes cannot silently bind the wrong channels.
        string[] names = { "Fcl_MTH_A", "Fcl_MTH_I", "Fcl_MTH_U", "Fcl_MTH_E", "Fcl_MTH_O", "Fcl_EYE_Close" };
        faceRenderer = humanoid.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(renderer => renderer.sharedMesh != null && names.All(name => renderer.sharedMesh.GetBlendShapeIndex(name) >= 0));
        Check(faceRenderer != null, "Could not find all actual NEVA mouth/blink blendshapes on one mesh");
        int[] indices = names.Select(name => faceRenderer.sharedMesh.GetBlendShapeIndex(name)).ToArray();
        Check(indices.Distinct().Count() == names.Length, "Mouth and blink channels overlap");
        dynamicExpressionIndices = new HashSet<int>(indices);
        mouthAIndex = indices[0];
        blinkIndex = indices[5];
        report.expressionMesh = faceRenderer.name + " / " + faceRenderer.sharedMesh.name;
        report.expressionBlendShapes = names.Select((name, i) => name + "=" + indices[i]).ToArray();

        silentAudioSource = humanoid.Root.AddComponent<AudioSource>();
        silentAudioSource.playOnAwake = false;
        silentAudioSource.clip = null;
        silentAudioSource.Stop();
        silentAudioSource.enabled = false;
        lips = humanoid.Root.AddComponent<Audio2LipScript>();
        lips.meshRenderer = faceRenderer;
        lips.blendWeightMultiplier = 100f;
        lips.m_VisemeIndex = new Audio2LipScript.VisemeBlenderShapeIndexMap
            { A = indices[0], I = indices[1], U = indices[2], E = indices[3], O = indices[4] };
        var frameField = typeof(Audio2LipScript).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(frameField != null, "Could not resolve Audio2LipScript's real Frame field");
        visemeFrame = (OVRLipSync.Frame)frameField.GetValue(lips);
        Check(visemeFrame != null && visemeFrame.Visemes.Length == OVRLipSync.VisemeCount, "Invalid actual lip-sync frame");
        var contextField = typeof(Audio2LipScript).GetField("Context", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(contextField != null && (uint)contextField.GetValue(lips) != 0, "Real Audio2Lip Awake could not create its native OVRLipSync context");

        blink = humanoid.Root.AddComponent<BlinkController>();
        blink.skinnedMeshRenderer = faceRenderer;
        blink.blinkBlendIndex = blinkIndex;
        blink.blinkWeight = 0f;
        blink.blinkInterval = 0.6f;
        blink.blinkDuration = 0.1f;
    }

    private static void InjectNextViseme()
    {
        Check(lips != null && lips.enabled && blink != null && blink.enabled, "Real expression components were disabled");
        lock (lips)
        {
            Array.Clear(visemeFrame.Visemes, 0, visemeFrame.Visemes.Length);
            float value = 0.5f + 0.45f * Mathf.Sin(Time.time * (2f * Mathf.PI / 0.8f));
            visemeFrame.Visemes[(int)OVRLipSync.Viseme.aa] = value;
            // Audio2LipScript intentionally truncates its computed weight to an integer.
            expectedMouthWeight = (int)(lips.blendWeightMultiplier * value);
            injectedAtFrame = Time.frameCount;
        }
    }

    private static void Sample()
    {
        Check(humanoid.Animator.enabled && control.Animator.enabled, "Motion playback disabled the original Animator");
        Check(!humanoid.Vrm.enabled && !control.Vrm.enabled, "Motion playback changed the private-scene VRM enabled configuration");
        Check(silentAudioSource != null && !silentAudioSource.enabled && !silentAudioSource.isPlaying, "Expression regression unexpectedly enabled audio playback");
        if (Time.frameCount > injectedAtFrame)
        {
            float error = Mathf.Abs(faceRenderer.GetBlendShapeWeight(mouthAIndex) - expectedMouthWeight);
            report.mouthExpectedWeightMaxError = Mathf.Max(report.mouthExpectedWeightMaxError, error);
            Check(error < 0.0001f, "Actual Audio2Lip Update did not preserve the preceding synthetic aa input through body playback");
            report.syntheticVisemeFramesVerified++;
        }
        foreach (var fixture in new[] { humanoid, control, rigBefore, rigAfter })
        {
            Check(Vector3.Distance(fixture.Root.transform.position, fixture.StartPosition) < 0.0001f, "Motion moved the avatar root");
            Check(Quaternion.Angle(fixture.Root.transform.rotation, fixture.StartRotation) < 0.1f, "Motion rotated the avatar root");
            foreach (var bone in ComparedBones)
            {
                var transform = fixture.Bone(bone);
                if (transform == null) continue;
                var q = transform.localRotation;
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                Check(!float.IsNaN(norm) && !float.IsInfinity(norm) && Mathf.Abs(norm - 1f) < 0.001f, "Nonunit/nonfinite rotation in actual player loop");
            }
        }
        foreach (var fixture in new[] { humanoid, control })
            foreach (var pair in fixture.Expressions)
                for (int i = 0; i < pair.Value.Length; i++)
                {
                    if (fixture == humanoid && pair.Key == faceRenderer && dynamicExpressionIndices.Contains(i)) continue;
                    Check(Mathf.Abs(pair.Key.GetBlendShapeWeight(i) - pair.Value[i]) < 0.0001f, "Body playback changed sentinel blendshape weight");
                }

        float elapsed = Time.time - phaseStartedAt;
        if (phase == 0)
        {
            if (elapsed > 0.45f)
            {
                foreach (var fixture in new[] { rigBefore, rigAfter })
                    foreach (var pair in fixture.Neutral)
                    {
                        float error = Quaternion.Angle(pair.Value, fixture.RelativeRotation(pair.Key));
                        report.controlRigNeutralMaxErrorDegrees = Mathf.Max(report.controlRigNeutralMaxErrorDegrees, error);
                        Check(error < 0.3f, "Identity source rest adds rotation to real ControlRig target: " + pair.Key);
                    }
            }
            if (elapsed < 0.8f) return;
            report.idlePngSha256 = Capture("playmode-idle.png");
            wristStart = humanoid.Bone(HumanBodyBones.LeftHand).position.y;
            animationStart = humanoid.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            humanoid.Player.Play(Clip(), ArdyMotionMask.LeftArm);
            rigBefore.Player.Play(Clip(), ArdyMotionMask.LeftArm);
            rigAfter.Player.Play(Clip(), ArdyMotionMask.LeftArm);
            AdvancePhase();
        }
        else if (phase == 1)
        {
            float mouthWeight = faceRenderer.GetBlendShapeWeight(mouthAIndex);
            float blinkWeight = faceRenderer.GetBlendShapeWeight(blinkIndex);
            report.wavingMouthMinimum = Mathf.Min(report.wavingMouthMinimum, mouthWeight);
            report.wavingMouthMaximum = Mathf.Max(report.wavingMouthMaximum, mouthWeight);
            report.wavingBlinkMinimum = Mathf.Min(report.wavingBlinkMinimum, blinkWeight);
            report.wavingBlinkMaximum = Mathf.Max(report.wavingBlinkMaximum, blinkWeight);
            report.humanoidWristRiseMetres = Mathf.Max(report.humanoidWristRiseMetres, humanoid.Bone(HumanBodyBones.LeftHand).position.y - wristStart);
            var a = humanoid.Root.transform.InverseTransformPoint(humanoid.Bone(HumanBodyBones.LeftHand).position);
            var b = control.Root.transform.InverseTransformPoint(control.Bone(HumanBodyBones.LeftHand).position);
            report.humanoidActionSeparationMetres = Mathf.Max(report.humanoidActionSeparationMetres, Vector3.Distance(a, b));
            float shoulder = rigBefore.Bone(HumanBodyBones.LeftUpperArm).position.y;
            report.controlRigWristAboveShoulderMetres = Mathf.Max(report.controlRigWristAboveShoulderMetres, rigBefore.Bone(HumanBodyBones.LeftHand).position.y - shoulder);
            if (elapsed > 0.5f)
            {
                foreach (var bone in new[] { HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand })
                {
                    if (humanoid.Bone(bone) == null || rigBefore.Bone(bone) == null) continue;
                    float error = Quaternion.Angle(humanoid.ChestRelativeRotation(bone), rigBefore.ChestRelativeRotation(bone));
                    report.humanoidToControlRigMaxErrorDegrees = Mathf.Max(report.humanoidToControlRigMaxErrorDegrees, error);
                    Check(error < 0.35f, "Late-bound Humanoid disagrees with known-rest ControlRig: " + bone + " (idle may have been captured as rest)");
                }
                foreach (var bone in ComparedBones)
                {
                    if (rigBefore.Bone(bone) == null || rigAfter.Bone(bone) == null) continue;
                    float error = Quaternion.Angle(rigBefore.RelativeRotation(bone), rigAfter.RelativeRotation(bone));
                    report.controlRigYawMaxErrorDegrees = Mathf.Max(report.controlRigYawMaxErrorDegrees, error);
                    Check(error < 0.35f, "ControlRig playback depends on when root yaw was applied: " + bone);
                }
            }
            if (elapsed < 2.2f) return;
            Check(report.humanoidWristRiseMetres > 0.15f, "Actual Animator overwrote the left-arm motion or wrist did not rise");
            Check(report.humanoidActionSeparationMetres > 0.15f, "Motion matches Animator-only control; body overlay was not visible");
            Check(report.controlRigWristAboveShoulderMetres > 0.04f, "VRM LateUpdate did not apply the control-rig wave to the mesh skeleton");
            Check(report.wavingMouthMaximum - report.wavingMouthMinimum > 30f, "Real Audio2Lip Update did not animate the mouth during waving");
            Check(report.wavingBlinkMaximum - report.wavingBlinkMinimum > 50f, "Real BlinkController did not blink during waving");
            report.wavePngSha256 = Capture("playmode-wave.png");
            Check(report.wavePngSha256 != report.idlePngSha256, "PlayMode idle and wave images are byte-identical");
            humanoid.Player.Stop();
            rigBefore.Player.Stop();
            rigAfter.Player.Stop();
            AdvancePhase();
        }
        else if (phase == 2)
        {
            if (elapsed < 0.55f) return;
            Check(!humanoid.Player.IsPlaying, "Stop did not finish in the actual player loop");
            CompareAnimatorControl();
            if (elapsed < 0.85f) return;
            report.stoppedPngSha256 = Capture("playmode-stopped.png");
            humanoid.Animator.SetInteger("state", 1);
            control.Animator.SetInteger("state", 1);
            AdvancePhase();
        }
        else
        {
            CompareAnimatorControl();
            if (elapsed < 1.2f) return;
            // State transitions prove the body was returned to a live controller, not a cached idle pose.
            Check(humanoid.Animator.GetCurrentAnimatorStateInfo(0).shortNameHash != Animator.StringToHash("idle"), "Animator did not transition to the real thinking state after Stop");
            Check(report.sampledFrames >= 20, "Too few actual player frames sampled");
            Check(report.syntheticVisemeFramesVerified >= 20, "Too few actual expression Update frames verified");
            Finish(null);
        }
    }

    private static void CompareAnimatorControl()
    {
        report.animatorNormalisedTimeAdvance = Mathf.Max(report.animatorNormalisedTimeAdvance,
            humanoid.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime - animationStart);
        foreach (var bone in ComparedBones)
        {
            if (humanoid.Bone(bone) == null || control.Bone(bone) == null) continue;
            float error = Quaternion.Angle(humanoid.Bone(bone).localRotation, control.Bone(bone).localRotation);
            report.returnToAnimatorMaxErrorDegrees = Mathf.Max(report.returnToAnimatorMaxErrorDegrees, error);
            Check(error < 0.4f, "Stopped playback differs from synchronized live Animator: " + bone);
        }
    }

    private static void AdvancePhase() { phase++; phaseStartedAt = Time.time; }
    private static TextAsset Clip()
    {
        var asset = Resources.Load<TextAsset>("ARDY/left-wave");
        Check(asset != null, "Missing real left-wave resource");
        return asset;
    }

    private static Camera CreateCamera(GameObject avatar)
    {
        var cameraObject = new GameObject("ARDY PlayMode Capture Camera");
        var result = cameraObject.AddComponent<Camera>();
        result.enabled = false;
        result.cullingMask = 1 << 31;
        result.clearFlags = CameraClearFlags.SolidColor;
        result.backgroundColor = new Color(0.78f, 0.83f, 0.87f);
        result.orthographic = true;
        result.orthographicSize = 1.2f;
        result.transform.position = avatar.transform.position + new Vector3(0f, 1f, 4f);
        result.transform.LookAt(avatar.transform.position + Vector3.up);
        result.nearClipPlane = 0.01f;
        result.farClipPlane = 20f;
        for (int i = 0; i < 2; i++)
        {
            var light = new GameObject("ARDY PlayMode Capture Light " + i).AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = i == 0 ? 1.1f : 0.65f;
            light.cullingMask = 1 << 31;
            light.transform.rotation = Quaternion.Euler(i == 0 ? 30f : 340f, i == 0 ? 200f : 25f, 0f);
        }
        return result;
    }

    private static string Capture(string filename)
    {
        var target = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(768, 768, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0);
            image.Apply(false, false);
            var pixels = image.GetPixels32();
            var background = pixels[0];
            Check(pixels.Count(p => Math.Abs(p.r - background.r) + Math.Abs(p.g - background.g) + Math.Abs(p.b - background.b) > 30) > pixels.Length / 200,
                "PlayMode capture is empty");
            byte[] bytes = image.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(Output, filename), bytes);
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(target);
        }
    }

    private static void Finish(Exception error)
    {
        if (SessionState.GetString(Key + "Stage", "") == "exiting") return;
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed: " + error.Message;
        if (error != null) Debug.LogException(error);
        if (lips != null)
        {
            lips.enabled = false;
            var contextField = typeof(Audio2LipScript).GetField("Context", BindingFlags.Instance | BindingFlags.NonPublic);
            if (contextField != null)
            {
                uint context = (uint)contextField.GetValue(lips);
                if (context != 0) { OVRLipSync.DestroyContext(context); contextField.SetValue(lips, (uint)0); }
            }
        }
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "playmode-regression.json"), JsonUtility.ToJson(report, true));
        SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1);
        SessionState.SetString(Key + "Stage", "exiting");
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        else EditorApplication.delayCall += FinalizeBatch;
    }

    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        int code = SessionState.GetInt(Key + "ExitCode", 1);
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string saved = SessionState.GetString(Key + "Scenes", "");
            var scenes = string.IsNullOrEmpty(saved) ? null : JsonUtility.FromJson<SavedScenes>(saved);
            if (scenes?.scenes != null && scenes.scenes.Length > 0 && scenes.scenes.All(scene => !string.IsNullOrEmpty(scene.path)))
                EditorSceneManager.RestoreSceneManagerSetup(scenes.scenes.Select(scene => new SceneSetup
                    { path = scene.path, isLoaded = scene.isLoaded, isActive = scene.isActive }).ToArray());
        }
        catch (Exception error) { code = 1; Debug.LogException(error); }
        Debug.Log("[ArdyMotionPlayModeRegression] Exit " + code + "; report: " + Path.Combine(Output, "playmode-regression.json"));
        EditorApplication.Exit(code);
    }

    private static void Check(bool condition, string message)
    {
        if (report != null) report.checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
