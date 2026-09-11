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
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>Actual PlayMode regression and face renders in the disposable TailSpringValidation project.</summary>
[InitializeOnLoad]
public static class NevaEyeTrackingRegression
{
    private const string Key = "NeEEvA.EyeTrackingRegression.";
    private const string AvatarPath = "Assets/Model/NEVA.vrm";
    private const string ControllerPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const string SourcePath = "Assets/AIChatTookit/Scripts/Motion/NevaEyeTracking.cs";
    private static readonly FieldInfo RuntimeField = typeof(Vrm10Instance).GetField("m_runtime", BindingFlags.Instance | BindingFlags.NonPublic);

    [Serializable] private sealed class DirectionSample
    {
        public string name;
        public Vector2 targetAngles;
        public Vector2 smoothedAngles;
        public Vector2 leftEyeAngles;
        public Vector2 rightEyeAngles;
    }

    [Serializable] private sealed class Report
    {
        public string status = "running";
        public string method = "Actual Unity PlayMode, real NEVA.vrm and Animator, disabled full VRM runtime. A synchronized avatar with the same active tail spring verifies all non-eye transforms and expression weights. Eye angles use original edit-mode eye orientation markers attached to the current head frame; the Animator-only avatar's eyes are not a neutral reference because its idle clip also rotates them.";
        public string limitations = "No physical headset, eye-tracking hardware, audio or chat service was used. Camera.main fallback represents the same transform used by a desktop camera or tracked XR camera. PNGs use a separate face observation camera while the gaze target moves; the head-frame test rotates the avatar's hierarchy with an unchanged world target.";
        public string avatarSha256;
        public string runtimeSha256;
        public string regressionSha256;
        public int sampledFrames;
        public int checks;
        public int enableCycles;
        public float elapsedSeconds;
        public float animatorTimeAdvance;
        public float maximumNonEyeRotationErrorDegrees;
        public float maximumNonEyePositionErrorMetres;
        public float maximumBlendshapeError;
        public float maximumHorizontalEyeAngleDegrees;
        public float maximumVerticalEyeAngleDegrees;
        public float maximumAnimatorEyeAngleDegrees;
        public float maximumDirectionErrorDegrees;
        public float smoothingIntermediateAngleDegrees;
        public float maximumReenableErrorDegrees;
        public bool cameraFallbackPassed;
        public bool movingHeadPassed;
        public int captureFrameRate = 12;
        public int animationFrames;
        public float[] animationFrameTimes;
        public DirectionSample[] directions;
        public string[] captures;
        public string[] errorLogs;
    }

    private sealed class Fixture
    {
        public GameObject Root;
        public Vrm10Instance Vrm;
        public Animator Animator;
        public Transform Head, Frame, Left, Right, LeftNeutral, RightNeutral;
        public Dictionary<string, Transform> Bones;
        public Dictionary<string, SkinnedMeshRenderer> Renderers;
        public Quaternion HeadRest;

        public Fixture(int index)
        {
            Root = GameObject.Find("EyeRegression-" + index);
            Check(Root != null, "Missing temporary eye regression avatar");
            Vrm = Root.GetComponent<Vrm10Instance>();
            Animator = Root.GetComponent<Animator>();
            Head = Animator.GetBoneTransform(HumanBodyBones.Head);
            Left = Animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Right = Animator.GetBoneTransform(HumanBodyBones.RightEye);
            Frame = Head.Find("RegressionFacingFrame");
            Check(Head != null && Left != null && Right != null && Frame != null, "Missing real eye bones or calibration frame");
            LeftNeutral = Frame.Find("LeftEyeNeutral");
            RightNeutral = Frame.Find("RightEyeNeutral");
            Check(LeftNeutral != null && RightNeutral != null, "Missing authored eye neutral references");
            HeadRest = Head.localRotation;
            Bones = Root.GetComponentsInChildren<Transform>(true).Where(t => t != Root.transform)
                .ToDictionary(t => AnimationUtility.CalculateTransformPath(t, Root.transform));
            Renderers = Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .ToDictionary(t => AnimationUtility.CalculateTransformPath(t.transform, Root.transform));
            foreach (var renderer in Renderers.Values)
            {
                renderer.updateWhenOffscreen = true;
                if (renderer.sharedMesh == null) continue;
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++) renderer.SetBlendShapeWeight(i, 2 + i % 5);
            }
            Animator.SetInteger("state", 0);
            Animator.Play("Base Layer.idle", 0, 0f);
        }
    }

    private static readonly string[] Names = { "front", "right", "left", "up", "down", "behind", "near", "camera-fallback", "missing", "head-turned" };
    private static readonly Vector2[] Angles = { Vector2.zero, new Vector2(45, 0), new Vector2(-45, 0), new Vector2(0, 35), new Vector2(0, -35), new Vector2(180, 0), Vector2.zero, new Vector2(30, -20), Vector2.zero, new Vector2(40, 20) };
    private static readonly List<DirectionSample> directions = new List<DirectionSample>();
    private static readonly List<string> captures = new List<string>();
    private static readonly List<string> errors = new List<string>();
    private static readonly List<float> frameTimes = new List<float>();
    private static Report report;
    private static Fixture subject, control;
    private static NevaEyeTracking tracker;
    private static Transform gaze;
    private static Camera observer, fallbackCamera;
    private static int phase, lastFrame;
    private static float phaseStarted, startedAt, animationStart, nextCapture;
    private static bool finishing, transitionSampled;
    private static Vector3 heldWorldTarget;
    private static Vector2 restartReference;

    static NevaEyeTrackingRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) SessionState.SetString(Key + "Stage", "ready");
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key + "Stage", "") == "exiting")
                EditorApplication.delayCall += FinalizeBatch;
        };
        Application.logMessageReceived += CaptureError;
        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool(Key + "Active", false) && !EditorApplication.isPlaying &&
                SessionState.GetString(Key + "Stage", "") == "exiting") FinalizeBatch();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || !Application.dataPath.Replace('\\', '/').Contains("/Logs/TailSpringValidation/"))
            throw new InvalidOperationException("Eye regression may only run in the isolated Logs/TailSpringValidation project.");
        try
        {
            string output = Argument("-eyeReportDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports/EyeTracking"));
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(Path.Combine(output, "motion-frames"));
            SessionState.SetString(Key + "Output", Path.GetFullPath(output));
            string source = Argument("-eyeSourceRoot");
            if (!string.IsNullOrEmpty(source))
                foreach (string file in new[] { AvatarPath, ControllerPath, SourcePath, "Assets/Editor/NevaEyeTrackingRegression.cs" })
                    Check(Hash(file) == Hash(Path.Combine(source, file)), "Isolated eye test asset differs from source: " + file);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var controllerAsset = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            Check(prefab != null && controllerAsset != null, "Missing real NEVA avatar or controller; use the imported tail validation project");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var target = new GameObject("EyeRegressionTarget").transform;
            for (int i = 0; i < 2; i++)
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                root.name = "EyeRegression-" + i;
                root.transform.SetPositionAndRotation(new Vector3(i * 4f, 0f, 0f), Quaternion.Euler(0f, 25f, 0f));
                var vrm = root.GetComponent<Vrm10Instance>();
                var animator = root.GetComponent<Animator>();
                Check(vrm != null && animator != null && animator.isHuman, "Expected imported humanoid VRM");
                vrm.enabled = false;
                animator.runtimeAnimatorController = controllerAsset;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
                animator.enabled = true;
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                var frame = new GameObject("RegressionFacingFrame").transform;
                frame.SetParent(head, false);
                frame.position = head.TransformPoint(vrm.Vrm.LookAt.OffsetFromHead);
                frame.rotation = root.transform.rotation;
                for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
                {
                    var eye = animator.GetBoneTransform(eyeIndex == 0 ? HumanBodyBones.LeftEye : HumanBodyBones.RightEye);
                    var neutral = new GameObject(eyeIndex == 0 ? "LeftEyeNeutral" : "RightEyeNeutral").transform;
                    neutral.SetParent(frame, false);
                    neutral.SetPositionAndRotation(eye.position, eye.rotation);
                }
                root.AddComponent<NevaTailSpring>().Configure(vrm);
                if (i == 0)
                {
                    target.position = frame.position + frame.forward * 2f;
                    root.AddComponent<NevaEyeTracking>().Configure(vrm, target);
                }
                foreach (var transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = i == 0 ? 31 : 30;
            }
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetString(Key + "Stage", "entering");
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 180f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetString(Key + "Stage", "") == "exiting") return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        {
            Finish(new TimeoutException("Eye regression timed out in the real player loop"));
            return;
        }
        if (!EditorApplication.isPlaying || EditorApplication.isPaused || SessionState.GetString(Key + "Stage", "") != "ready") return;
        try
        {
            if (report == null)
            {
                if (Time.time >= 0.5f) Initialize();
                return;
            }
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            report.sampledFrames++;
            report.elapsedSeconds = Time.time - startedAt;
            Check(errors.Count == 0, "Unity runtime errors: " + string.Join(" | ", errors));
            CheckInvariants();
            Tick();
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static void Initialize()
    {
        report = new Report { avatarSha256 = Hash(AvatarPath), runtimeSha256 = Hash(SourcePath), regressionSha256 = Hash("Assets/Editor/NevaEyeTrackingRegression.cs") };
        subject = new Fixture(0);
        control = new Fixture(1);
        tracker = subject.Root.GetComponent<NevaEyeTracking>();
        gaze = GameObject.Find("EyeRegressionTarget").transform;
        Check(tracker != null && tracker.IsInitialized, "Eye tracker did not initialize in actual PlayMode");
        Check(tracker.Target == subject.Vrm && tracker.Head == subject.Head && tracker.LeftEye == subject.Left && tracker.RightEye == subject.Right,
            "Eye tracker bound to the wrong avatar or bones");
        Check(subject.Root.GetComponent<NevaTailSpring>().IsInitialized && control.Root.GetComponent<NevaTailSpring>().IsInitialized, "Existing tail springs did not initialize");
        observer = CreateObserver();
        animationStart = subject.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        startedAt = phaseStarted = Time.time;
        SetTarget(Angles[0]);
    }

    private static void Tick()
    {
        float elapsed = Time.time - phaseStarted;
        if (phase < Names.Length)
        {
            if (phase == 1 && !transitionSampled && elapsed > 0.035f && elapsed < 0.3f)
            {
                report.smoothingIntermediateAngleDegrees = tracker.CurrentInputAngles.x;
                Check(report.smoothingIntermediateAngleDegrees > 0.05f && report.smoothingIntermediateAngleDegrees < 44.8f,
                    "A sudden target change should take intermediate angles before settling");
                transitionSampled = true;
            }
            if (elapsed >= 1.3f)
            {
                bool neutral = phase == 5 || phase == 6 || phase == 8;
                Vector2 expected = neutral ? Vector2.zero : TargetAngles(phase == 7 ? fallbackCamera.transform.position : gaze.position);
                ValidateDirection(Names[phase], expected);
                if (phase < 5) Capture("eyes-" + Names[phase] + ".png");
                if (phase == 7) report.cameraFallbackPassed = true;
                if (phase == 9) report.movingHeadPassed = true;
                phase++;
                phaseStarted = Time.time;
                if (phase == 5) report.animatorTimeAdvance = subject.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime - animationStart;
                BeginPhase();
            }
            else if (phase != 8 && phase != 9) SetTarget(Angles[phase], phase == 6 ? 0.05f : 2f);
            return;
        }

        // Three repeated enable cycles use the same held target and unchanged head pose.
        if (phase < Names.Length + 6)
        {
            bool disabled = (phase - Names.Length) % 2 == 0;
            if (disabled && elapsed >= 0.2f)
            {
                Check(Quaternion.Angle(subject.Left.localRotation, control.Left.localRotation) < 0.2f &&
                    Quaternion.Angle(subject.Right.localRotation, control.Right.localRotation) < 0.2f,
                    "After disabling, eye bones should return to the synchronized Animator's control");
                tracker.enabled = true;
                phase++;
                phaseStarted = Time.time;
            }
            else if (!disabled && elapsed >= 1.3f)
            {
                ValidateDirection("reenable-" + report.enableCycles, TargetAngles(gaze.position));
                float error = Vector2.Distance(EyeAngles(subject.Left, control.Left), restartReference);
                report.maximumReenableErrorDegrees = Mathf.Max(error, report.maximumReenableErrorDegrees);
                Check(error < 0.3f, "Re-enabling accumulated a different eye neutral rotation");
                report.enableCycles++;
                phase++;
                phaseStarted = Time.time;
                if (phase < Names.Length + 6) DisableAndCheckRest();
                else
                {
                    Check(transitionSampled, "Missed smoothing samples in the actual player loop");
                    Check(report.animatorTimeAdvance > 0.1f, "Real Animator did not advance");
                    Time.captureFramerate = 60;
                    nextCapture = 0f;
                }
            }
            return;
        }

        const float duration = 5f;
        if (elapsed >= duration)
        {
            Check(frameTimes.Count >= 55 || observer == null, "Insufficient actual motion captures");
            Finish(null);
            return;
        }
        if (elapsed >= nextCapture && observer != null)
        {
            Capture("motion-frames/eyes-" + frameTimes.Count.ToString("D3") + ".png", 640);
            frameTimes.Add(elapsed);
            nextCapture += 1f / 12f;
        }
        float angle = elapsed * Mathf.PI * 2f / duration;
        SetTarget(new Vector2(55f * Mathf.Sin(angle), 25f * Mathf.Sin(angle * 2f)));
    }

    private static void BeginPhase()
    {
        if (phase < Names.Length)
        {
            tracker.gazeTarget = gaze;
            if (phase == 7)
            {
                fallbackCamera = new GameObject("Eye regression MainCamera fallback").AddComponent<Camera>();
                fallbackCamera.tag = "MainCamera";
                fallbackCamera.cullingMask = 0;
                fallbackCamera.clearFlags = CameraClearFlags.Nothing;
                tracker.gazeTarget = null;
            }
            if (phase == 8)
            {
                fallbackCamera.enabled = false;
                fallbackCamera.tag = "Untagged";
                tracker.gazeTarget = null;
            }
            if (phase == 9)
            {
                subject.Animator.speed = control.Animator.speed = 0f;
                SetTarget(Angles[phase]);
                heldWorldTarget = gaze.position;
                // Rotate the current head frame through its hierarchy. Animator speed zero still
                // writes its sampled pose, so assigning the head bone outside the player loop
                // would not test a lasting pose change.
                Quaternion changed = subject.Root.transform.rotation * Quaternion.Euler(12f, 25f, 8f);
                subject.Root.transform.rotation = control.Root.transform.rotation = changed;
                gaze.position = heldWorldTarget;
            }
            else if (phase != 8) SetTarget(Angles[phase], phase == 6 ? 0.05f : 2f);
        }
        else
        {
            restartReference = EyeAngles(subject.Left, control.Left);
            DisableAndCheckRest();
        }
    }

    private static void DisableAndCheckRest()
    {
        tracker.enabled = false;
        Check(EyeAngles(subject.Left, control.Left).magnitude < 0.2f && EyeAngles(subject.Right, control.Right).magnitude < 0.2f,
            "The disable callback must immediately restore the authored neutral before Animator resumes");
    }

    private static void SetTarget(Vector2 input, float distance = 2f)
    {
        Vector3 direction = Quaternion.Euler(-input.y, input.x, 0f) * Vector3.forward;
        gaze.position = subject.Frame.position + subject.Frame.TransformDirection(direction) * distance;
        if (phase == 7 && fallbackCamera != null) fallbackCamera.transform.position = gaze.position;
    }

    private static Vector2 TargetAngles(Vector3 point)
    {
        Vector3 direction = subject.Frame.InverseTransformDirection(point - subject.Frame.position);
        return new Vector2(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg,
            Mathf.Atan2(direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg);
    }

    private static Vector2 EyeAngles(Transform actual, Transform unusedAnimatorEye)
    {
        Transform neutral = actual == subject.Left ? subject.LeftNeutral : subject.RightNeutral;
        Quaternion delta = Quaternion.Inverse(subject.Frame.rotation) * actual.rotation * Quaternion.Inverse(neutral.rotation) * subject.Frame.rotation;
        Vector3 forward = delta * Vector3.forward;
        return new Vector2(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg,
            Mathf.Atan2(forward.y, Mathf.Sqrt(forward.x * forward.x + forward.z * forward.z)) * Mathf.Rad2Deg);
    }

    private static void ValidateDirection(string name, Vector2 expected)
    {
        Vector2 left = EyeAngles(subject.Left, control.Left), right = EyeAngles(subject.Right, control.Right);
        report.maximumDirectionErrorDegrees = Mathf.Max(report.maximumDirectionErrorDegrees, Vector2.Distance(expected, tracker.CurrentInputAngles));
        Check(Vector2.Distance(expected, tracker.CurrentInputAngles) < 1.2f, "Incorrect settled gaze input for " + name + ": expected " + expected + ", got " + tracker.CurrentInputAngles);
        var settings = subject.Vrm.Vrm.LookAt;
        float leftYaw = Mathf.Sign(expected.x) * (expected.x < 0 ? settings.HorizontalOuter : settings.HorizontalInner).Map(Mathf.Abs(expected.x));
        float rightYaw = Mathf.Sign(expected.x) * (expected.x < 0 ? settings.HorizontalInner : settings.HorizontalOuter).Map(Mathf.Abs(expected.x));
        float pitch = Mathf.Sign(expected.y) * (expected.y < 0 ? settings.VerticalDown : settings.VerticalUp).Map(Mathf.Abs(expected.y));
        Check(Vector2.Distance(left, new Vector2(leftYaw, pitch)) < 0.35f && Vector2.Distance(right, new Vector2(rightYaw, pitch)) < 0.35f,
            "Actual eye transforms disagree with the avatar's VRM eye ranges for " + name + ": left=" + left + ", right=" + right);
        directions.Add(new DirectionSample { name = name, targetAngles = expected, smoothedAngles = tracker.CurrentInputAngles, leftEyeAngles = left, rightEyeAngles = right });
    }

    private static void CheckInvariants()
    {
        report.maximumAnimatorEyeAngleDegrees = Mathf.Max(report.maximumAnimatorEyeAngleDegrees,
            Quaternion.Angle(control.Left.rotation, control.LeftNeutral.rotation),
            Quaternion.Angle(control.Right.rotation, control.RightNeutral.rotation));
        Check(!subject.Vrm.enabled && !control.Vrm.enabled && RuntimeField != null && RuntimeField.GetValue(subject.Vrm) == null && RuntimeField.GetValue(control.Vrm) == null,
            "Eye tracking or tail spring started the full VRM runtime");
        foreach (var pair in subject.Bones)
        {
            if (pair.Value == subject.Left || pair.Value == subject.Right) continue;
            Transform other = control.Bones[pair.Key];
            float rotationError = Quaternion.Angle(pair.Value.localRotation, other.localRotation);
            float positionError = Vector3.Distance(pair.Value.localPosition, other.localPosition);
            report.maximumNonEyeRotationErrorDegrees = Mathf.Max(report.maximumNonEyeRotationErrorDegrees, rotationError);
            report.maximumNonEyePositionErrorMetres = Mathf.Max(report.maximumNonEyePositionErrorMetres, positionError);
            Check(rotationError < 0.25f && positionError < 0.0005f, "Eye tracking changed a non-eye transform: " + pair.Key);
        }
        foreach (var pair in subject.Renderers)
        {
            var renderer = pair.Value;
            if (renderer.sharedMesh == null) continue;
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                float difference = Mathf.Abs(renderer.GetBlendShapeWeight(i) - control.Renderers[pair.Key].GetBlendShapeWeight(i));
                report.maximumBlendshapeError = Mathf.Max(report.maximumBlendshapeError, difference);
                Check(difference < 0.001f, "Eye tracking overwrote a facial expression weight");
            }
        }
        foreach (Vector2 eye in new[] { EyeAngles(subject.Left, control.Left), EyeAngles(subject.Right, control.Right) })
        {
            report.maximumHorizontalEyeAngleDegrees = Mathf.Max(report.maximumHorizontalEyeAngleDegrees, Mathf.Abs(eye.x));
            report.maximumVerticalEyeAngleDegrees = Mathf.Max(report.maximumVerticalEyeAngleDegrees, Mathf.Abs(eye.y));
            Check(Mathf.Abs(eye.x) < 12.3f && Mathf.Abs(eye.y) < 10.3f, "Eye rotation exceeded the model's VRoid limits");
        }
    }

    private static Camera CreateObserver()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
        var result = new GameObject("Eye regression face observer").AddComponent<Camera>();
        result.enabled = false;
        result.cullingMask = 1 << 31;
        result.clearFlags = CameraClearFlags.SolidColor;
        result.backgroundColor = new Color(0.25f, 0.27f, 0.29f);
        result.orthographic = true;
        result.orthographicSize = 0.18f;
        result.nearClipPlane = 0.01f;
        result.farClipPlane = 10f;
        Vector3 centre = (subject.Left.position + subject.Right.position) * 0.5f + subject.Root.transform.up * 0.015f;
        result.transform.position = centre + subject.Root.transform.forward * 1.5f;
        result.transform.LookAt(centre, subject.Root.transform.up);
        for (int i = 0; i < 2; i++)
        {
            var light = new GameObject("Eye regression face light " + i).AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = i == 0 ? 1.0f : 0.45f;
            light.cullingMask = 1 << 31;
            light.transform.rotation = Quaternion.Euler(30f, i == 0 ? 205f : 20f, 0f);
        }
        return result;
    }

    private static void Capture(string name, int size = 768)
    {
        if (observer == null) return;
        Vector3 centre = (subject.Left.position + subject.Right.position) * 0.5f + subject.Frame.up * 0.015f;
        observer.transform.position = centre + subject.Frame.forward * 1.5f;
        observer.transform.LookAt(centre, subject.Frame.up);
        var texture = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(size, size, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            observer.targetTexture = texture;
            observer.Render();
            RenderTexture.active = texture;
            image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            image.Apply(false, false);
            File.WriteAllBytes(Path.Combine(Output, name), image.EncodeToPNG());
            captures.Add(name);
        }
        finally
        {
            observer.targetTexture = null;
            RenderTexture.active = previous;
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(texture);
        }
    }

    private static string Output => SessionState.GetString(Key + "Output", Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports/EyeTracking")));
    private static string Argument(string flag)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, flag);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static void CaptureError(string condition, string stack, LogType type)
    {
        if (!SessionState.GetBool(Key + "Active", false) || finishing) return;
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert || condition.IndexOf("has not been disposed", StringComparison.OrdinalIgnoreCase) >= 0)
            errors.Add(condition + "\n" + stack);
    }
    private static void Finish(Exception exception)
    {
        if (finishing) return;
        finishing = true;
        Time.captureFramerate = 0;
        if (report == null) report = new Report();
        report.status = exception == null ? "passed" : "failed: " + exception.Message;
        report.directions = directions.ToArray();
        report.captures = captures.ToArray();
        report.errorLogs = errors.ToArray();
        report.animationFrameTimes = frameTimes.ToArray();
        report.animationFrames = frameTimes.Count;
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "eye-tracking-regression.json"), JsonUtility.ToJson(report, true));
        if (exception != null) Debug.LogException(exception);
        SessionState.SetBool(Key + "Active", true);
        SessionState.SetInt(Key + "ExitCode", exception == null ? 0 : 1);
        SessionState.SetString(Key + "Stage", "exiting");
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        int code = SessionState.GetInt(Key + "ExitCode", 1);
        Debug.Log("[NevaEyeTrackingRegression] Exit " + code + "; report: " + Path.Combine(Output, "eye-tracking-regression.json"));
        EditorApplication.Exit(code);
    }
    private static void Check(bool value, string message)
    {
        if (report != null) report.checks++;
        if (!value) throw new InvalidOperationException(message);
    }
}
