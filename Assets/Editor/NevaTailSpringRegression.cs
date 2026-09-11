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

/// <summary>
/// Real player-loop tail regression. Run an isolated Logs/TailSpringValidation project using
/// -batchmode -executeMethod NevaTailSpringRegression.RunBatch (without -quit).
/// Optional: -tailReportDir ABSOLUTE_PATH -tailSourceRoot ORIGINAL_PROJECT_PATH.
/// </summary>
[InitializeOnLoad]
public static class NevaTailSpringRegression
{
    private const string Key = "NeEEvA.TailSpringRegression.";
    private const string AvatarAsset = "Assets/Model/NEVA.vrm";
    private const string ControllerAsset = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const string RuntimeSource = "Assets/AIChatTookit/Scripts/Motion/NevaTailSpring.cs";
    private const string MotionAssembly = "Assets/AIChatTookit/Scripts/Motion/NeEEvA.Motion.asmdef";
    private const string RegressionSource = "Assets/Editor/NevaTailSpringRegression.cs";
    private const int IdleCaptureFrameRate = 12;
    private static readonly FieldInfo RuntimeField = typeof(Vrm10Instance).GetField("m_runtime", BindingFlags.Instance | BindingFlags.NonPublic);

    [Serializable] private sealed class Report
    {
        public string status = "running";
        public string sourceAvatar = AvatarAsset;
        public string animatorController = ControllerAsset;
        public string method = "Actual Unity PlayMode, Animator and LateUpdate; no manual Animator.Update, physics ticks or Vrm.Runtime access. An identical synchronized Animator-only avatar verifies all non-tail transforms and blendshape sentinel weights. Idle distribution measures all six joint positions and baked mesh centroids in fixed root/middle/tip regions selected from the original straight tail, with at least 75% FoxTail skin weight.";
        public string limitation = "Isolated avatar with the private scene's disabled Vrm10Instance; no chat, mouth audio, headset or scene-level furniture collision testing. Idle sway and lift are disabled only for deterministic convergence measurement. Idle measurements begin after one warm-up cycle with the Animator frozen.";
        public int sampledFrames;
        public int invariantFrames;
        public int checks;
        public int restartCycles;
        public float elapsedSeconds;
        public float animatorTimeAdvance;
        public float tailTipDropMetres;
        public float tailMotionMetres;
        public float maximumBoneLengthErrorMetres;
        public float maximumNonTailRotationErrorDegrees;
        public float maximumNonTailPositionErrorMetres;
        public float maximumBlendshapeError;
        public float settledTipMovementMetres;
        public float maximumRestartTipErrorMetres;
        public float defaultIdleSwayDegrees;
        public float defaultIdleLiftDegrees;
        public float defaultIdleSwayPeriodSeconds;
        public float idleSwayObservedSeconds;
        public float idleSwayTipSpanMetres;
        public float idleSwayLateralSpanMetres;
        public float idleSwayForeAftSpanMetres;
        public float maximumIdleSwayFromRestMetres;
        public float[] idleJointLateralSpansMetres;
        public string[] visibleTailRegions = { "root (first 20% of original mesh length)", "middle (35-65%)", "tip (last 20%)" };
        public int[] visibleTailRegionVertexCounts;
        public float[] visibleTailRegionLateralSpansMetres;
        public int visibleTailSamples;
        public int idleCaptureFrameRate = IdleCaptureFrameRate;
        public int idleCaptureFrames;
        public float[] idleCaptureTimesSeconds;
        public string avatarSha256;
        public string controllerSha256;
        public string runtimeSourceSha256;
        public string motionAssemblySha256;
        public string regressionSourceSha256;
        public string[] jointNames;
        public string[] errorLogs;
        public string[] captures;
    }

    private sealed class Fixture
    {
        public GameObject Root;
        public Vrm10Instance Vrm;
        public Animator Animator;
        public Dictionary<string, Transform> Transforms;
        public Dictionary<string, SkinnedMeshRenderer> Renderers;
        public Vector3 Origin;

        public Fixture(string name)
        {
            Root = GameObject.Find(name);
            Check(Root != null, "Missing temporary avatar " + name);
            Vrm = Root.GetComponent<Vrm10Instance>();
            Animator = Root.GetComponent<Animator>();
            Check(Vrm != null && Animator != null, "Missing imported VRM or Animator");
            Origin = Root.transform.position;
            Transforms = Root.GetComponentsInChildren<Transform>(true).Where(t => t != Root.transform)
                .ToDictionary(t => AnimationUtility.CalculateTransformPath(t, Root.transform));
            Renderers = Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .ToDictionary(r => AnimationUtility.CalculateTransformPath(r.transform, Root.transform));
            foreach (var renderer in Renderers.Values)
            {
                renderer.updateWhenOffscreen = true;
                if (renderer.sharedMesh == null) continue;
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                    renderer.SetBlendShapeWeight(i, 3f + i % 13);
            }
            Animator.SetInteger("state", 0);
            Animator.Play("Base Layer.idle", 0, 0f);
        }
    }

    private sealed class TailSkinProbe
    {
        public SkinnedMeshRenderer Renderer;
        public Mesh Baked;
        public int[] VertexRegions;
        public readonly List<Vector3> Vertices = new List<Vector3>();
    }

    private static Report report;
    private static Fixture subject, control;
    private static NevaTailSpring tail;
    private static Transform[] chain;
    private static float[] boneLengths;
    private static readonly List<string> errors = new List<string>();
    private static readonly List<string> captures = new List<string>();
    private static readonly Queue<Vector3> settledSamples = new Queue<Vector3>();
    private static readonly List<float> idleCaptureTimes = new List<float>();
    private static int phase, lastFrame, restartCycles;
    private static float startedAt, phaseStartedAt, animationStart;
    private static Vector3 restTip, lastTip;
    private static Vector3 swayMinimum, swayMaximum;
    private static Vector3[] jointMinimum, jointMaximum;
    private static Vector3[] visibleMinimum, visibleMaximum;
    private static readonly List<TailSkinProbe> skinProbes = new List<TailSkinProbe>();
    private static Vector3 backwardLocal;
    private static bool idleMeasurementStarted;
    private static float nextIdleCaptureTime;
    private static Camera camera;
    private static Camera rearCamera;
    private static bool finishing;

    static NevaTailSpringRegression()
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
            throw new InvalidOperationException("Run this regression only in an isolated Logs/TailSpringValidation project.");
        try
        {
            Check(!EditorApplication.isPlayingOrWillChangePlaymode, "Another PlayMode session is running");
            string output = Argument("-tailReportDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports"));
            Directory.CreateDirectory(output);
            SessionState.SetString(Key + "Output", Path.GetFullPath(output));
            VerifySourceFiles();
            var prefab = LoadAvatarAfterShaders();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerAsset);
            Check(prefab != null && controller != null, "Missing real NEVA.vrm or its Animator controller");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (int i = 0; i < 2; i++)
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                root.name = "TailRegression-" + i;
                root.transform.SetPositionAndRotation(new Vector3(i * 4f, 0f, 0f), Quaternion.Euler(0f, 25f, 0f));
                var vrm = root.GetComponent<Vrm10Instance>();
                Check(vrm != null, "NEVA.vrm did not import as Vrm10Instance");
                vrm.enabled = false;
                var animator = root.GetComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;
                animator.enabled = true;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = i == 0 ? 31 : 30;
            }
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetString(Key + "Stage", "entering");
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 120f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception)
        {
            report = new Report();
            Finish(exception);
        }
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        if (SessionState.GetString(Key + "Stage", "") == "exiting") return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        {
            Finish(new TimeoutException("Tail regression timed out in the real player loop"));
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
            if (Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;
            report.sampledFrames++;
            report.elapsedSeconds = Time.time - startedAt;
            Check(errors.Count == 0, "Unity reported errors: " + string.Join(" | ", errors));
            Sample();
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static void Initialize()
    {
        report = new Report
        {
            avatarSha256 = Hash(AvatarAsset), controllerSha256 = Hash(ControllerAsset),
            runtimeSourceSha256 = Hash(RuntimeSource), motionAssemblySha256 = Hash(MotionAssembly),
            regressionSourceSha256 = Hash(RegressionSource)
        };
        subject = new Fixture("TailRegression-0");
        control = new Fixture("TailRegression-1");
        Check(RuntimeField != null, "Cannot inspect disabled VRM runtime without constructing it");
        var transforms = subject.Root.GetComponentsInChildren<Transform>(true);
        chain = Enumerable.Range(1, 5).Select(i => transforms.Single(t => t.name == "J_Opt_C_FoxTail" + i + "_01")).ToArray();
        var end = transforms.SingleOrDefault(t => t.name == "J_Opt_C_FoxTail5_end_01");
        Check(end != null, "FoxTail5 has no end transform");
        chain = chain.Concat(new[] { end }).ToArray();
        backwardLocal = subject.Root.transform.InverseTransformDirection(
            Vector3.ProjectOnPlane(end.position - chain[0].position, subject.Root.transform.up).normalized);
        boneLengths = Enumerable.Range(0, chain.Length - 1).Select(i => Vector3.Distance(chain[i].position, chain[i + 1].position)).ToArray();
        Check(boneLengths.All(length => length > 0.001f), "Tail has a zero-length segment");
        report.jointNames = chain.Select(t => t.name).ToArray();
        InitializeSkinProbes();
        camera = CreateCamera();
        Capture("tail-before.png");
        tail = subject.Root.AddComponent<NevaTailSpring>();
        Check(tail.IsInitialized, "Tail did not automatically initialize on its own avatar");
        report.defaultIdleSwayDegrees = tail.idleSwayDegrees;
        report.defaultIdleLiftDegrees = tail.idleLiftDegrees;
        report.defaultIdleSwayPeriodSeconds = tail.idleSwayPeriod;
        Check(Mathf.Abs(tail.idleSwayDegrees - 12f) < 0.001f && Mathf.Abs(tail.idleLiftDegrees - 1f) < 0.001f &&
            Mathf.Abs(tail.idleSwayPeriod - 3.2f) < 0.001f,
            "Expected the authored defaults: 12 degrees distal sway, 1 degree lift, 3.2-second period");
        var serialized = new SerializedObject(tail);
        var sway = serialized.FindProperty("idleSwayDegrees");
        var lift = serialized.FindProperty("idleLiftDegrees");
        Check(sway != null && lift != null, "Missing adjustable idle sway or lift");
        sway.floatValue = 0f;
        lift.floatValue = 0f;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        tail.Bind(subject.Vrm);
        Check(tail.IsInitialized, "Tail did not initialize with disabled VRM");
        Check(tail.Joints.Count >= 5 && chain.Take(5).All(t => tail.Joints.Contains(t)), "Tail did not bind FoxTail1 through FoxTail5");
        startedAt = phaseStartedAt = Time.time;
        animationStart = subject.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        lastTip = TipLocal();
    }

    private static void Sample()
    {
        float elapsed = Time.time - phaseStartedAt;
        if (phase <= 6) VerifyIsolationAndLengths();
        if (phase == 0)
        {
            report.animatorTimeAdvance = subject.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime - animationStart;
            if (elapsed < 2.5f) return;
            Check(report.animatorTimeAdvance > 0.1f, "Original Animator did not advance in PlayMode");
            report.tailTipDropMetres = chain[0].position.y - chain[chain.Length - 1].position.y;
            Check(report.tailTipDropMetres > 0.2f, "Tail tip failed to hang at least 20 cm below the tail root");
            Capture("tail-natural.png");
            Advance();
        }
        else if (phase == 1)
        {
            // Moving both models identically retains a valid synchronized animation control.
            float t = Mathf.Clamp01(elapsed / 1.5f);
            Vector3 offset = new Vector3(Mathf.Sin(t * Mathf.PI) * 0.35f, 0.08f * Mathf.Sin(t * Mathf.PI), t * 0.5f);
            Quaternion rotation = Quaternion.Euler(0f, 25f + 75f * t, 0f);
            subject.Root.transform.SetPositionAndRotation(subject.Origin + offset, rotation);
            control.Root.transform.SetPositionAndRotation(control.Origin + offset, rotation);
            var tip = TipLocal();
            report.tailMotionMetres = Mathf.Max(report.tailMotionMetres, Vector3.Distance(tip, lastTip));
            if (elapsed < 1.5f) return;
            subject.Animator.speed = control.Animator.speed = 0f;
            settledSamples.Clear();
            Advance();
        }
        else if (phase == 2)
        {
            if (elapsed > 2f) settledSamples.Enqueue(TipLocal());
            if (elapsed < 3f) return;
            Check(settledSamples.Count >= 3, "Too few convergence samples");
            restTip = TipLocal();
            report.settledTipMovementMetres = settledSamples.Max(p => Vector3.Distance(p, restTip));
            Check(report.settledTipMovementMetres < 0.025f, "Tail failed to settle after model translation/rotation stopped");
            Check(report.tailMotionMetres > 0.005f, "Root movement did not produce any tail response");
            tail.enabled = false;
            Advance();
        }
        else if (phase == 3)
        {
            if (elapsed < 0.15f) return;
            Check(!tail.enabled, "Tail did not remain disabled");
            tail.enabled = true;
            Check(tail.IsInitialized, "Tail did not reinitialize after being enabled");
            Advance();
        }
        else if (phase == 4)
        {
            if (elapsed < 2.5f) return;
            float error = Vector3.Distance(TipLocal(), restTip);
            report.maximumRestartTipErrorMetres = Mathf.Max(report.maximumRestartTipErrorMetres, error);
            Check(error < 0.025f, "Disable/re-enable accumulated tail bending: " + error + " metres");
            report.restartCycles = ++restartCycles;
            if (restartCycles < 3)
            {
                tail.enabled = false;
                phase = 3;
                phaseStartedAt = Time.time;
            }
            else
            {
                tail.Rebuild();
                Check(tail.IsInitialized, "Rebuild did not restore initialized physics");
                Advance();
            }
        }
        else if (phase == 5)
        {
            if (elapsed < 2.5f) return;
            float error = Vector3.Distance(TipLocal(), restTip);
            report.maximumRestartTipErrorMetres = Mathf.Max(report.maximumRestartTipErrorMetres, error);
            Check(error < 0.025f, "Rebuild accumulated tail bending: " + error + " metres");
            // Restore both defaults and observe two cycles after a full warm-up cycle.
            // Animator remains at its synchronized stationary pose so measured motion is the sway.
            tail.idleSwayDegrees = report.defaultIdleSwayDegrees;
            tail.idleLiftDegrees = report.defaultIdleLiftDegrees;
            tail.idleSwayPeriod = report.defaultIdleSwayPeriodSeconds;
            PrepareIdleCamera();
            Advance();
        }
        else if (phase == 6)
        {
            float observed = elapsed - report.defaultIdleSwayPeriodSeconds;
            if (observed < 0f) return;
            Vector3 tip = TipLocal();
            if (!idleMeasurementStarted)
            {
                idleMeasurementStarted = true;
                swayMinimum = swayMaximum = tip;
                jointMinimum = chain.Select(JointLocal).ToArray();
                jointMaximum = (Vector3[])jointMinimum.Clone();
                report.idleJointLateralSpansMetres = new float[chain.Length];
                nextIdleCaptureTime = 0f;
            }
            for (int i = 0; i < chain.Length; i++)
            {
                Vector3 joint = JointLocal(chain[i]);
                jointMinimum[i] = Vector3.Min(jointMinimum[i], joint);
                jointMaximum[i] = Vector3.Max(jointMaximum[i], joint);
                report.idleJointLateralSpansMetres[i] = jointMaximum[i].x - jointMinimum[i].x;
            }
            swayMinimum = Vector3.Min(swayMinimum, tip);
            swayMaximum = Vector3.Max(swayMaximum, tip);
            report.idleSwayObservedSeconds = observed;
            report.idleSwayTipSpanMetres = Vector3.Distance(swayMinimum, swayMaximum);
            report.idleSwayLateralSpanMetres = swayMaximum.x - swayMinimum.x;
            report.idleSwayForeAftSpanMetres = swayMaximum.z - swayMinimum.z;
            report.maximumIdleSwayFromRestMetres = Mathf.Max(report.maximumIdleSwayFromRestMetres,
                Vector3.Distance(tip, restTip));
            Check(report.maximumIdleSwayFromRestMetres < 0.30f && report.idleSwayTipSpanMetres < 0.30f,
                "Default idle motion became too large or diverged");
            if (observed >= nextIdleCaptureTime)
            {
                SampleVisibleTail();
                string frame = "frame-" + report.idleCaptureFrames.ToString("D4") + ".png";
                Capture("idle-frames/" + frame, 512);
                Capture("idle-rear-frames/" + frame, 512, rearCamera);
                idleCaptureTimes.Add(observed);
                report.idleCaptureFrames++;
                nextIdleCaptureTime = report.idleCaptureFrames / (float)IdleCaptureFrameRate;
            }
            if (observed < 2f * report.defaultIdleSwayPeriodSeconds) return;
            ValidateIdleDistribution();
            if (camera != null)
                Check(report.idleCaptureFrames >= Mathf.CeilToInt(2f * report.defaultIdleSwayPeriodSeconds * IdleCaptureFrameRate),
                    "Too few 12 fps renders to demonstrate two full idle cycles");
            Object.Destroy(subject.Root);
            Object.Destroy(control.Root);
            foreach (var probe in skinProbes) Object.Destroy(probe.Baked);
            Advance();
        }
        else if (elapsed >= 0.4f)
        {
            Check(subject.Root == null && control.Root == null, "Temporary avatars were not destroyed");
            Check(errors.Count == 0, "Errors while disposing tail jobs/buffers");
            Check(report.invariantFrames >= 30, "Too few real frames verified");
            Finish(null);
        }
    }

    private static void VerifyIsolationAndLengths()
    {
        Check(!subject.Vrm.enabled && !control.Vrm.enabled, "Tail enabled full VRM updates");
        Check(RuntimeField.GetValue(subject.Vrm) == null && RuntimeField.GetValue(control.Vrm) == null, "Tail constructed a full Vrm10Runtime");
        foreach (var pair in subject.Transforms)
        {
            Transform actual = pair.Value;
            if (chain.Contains(actual)) continue;
            Transform expected = control.Transforms[pair.Key];
            float rotation = Quaternion.Angle(actual.localRotation, expected.localRotation);
            float position = Vector3.Distance(actual.localPosition, expected.localPosition);
            report.maximumNonTailRotationErrorDegrees = Mathf.Max(report.maximumNonTailRotationErrorDegrees, rotation);
            report.maximumNonTailPositionErrorMetres = Mathf.Max(report.maximumNonTailPositionErrorMetres, position);
            Check(rotation < 0.15f && position < 0.0002f && Vector3.Distance(actual.localScale, expected.localScale) < 0.0001f,
                "Tail changed a non-tail transform: " + pair.Key);
        }
        foreach (var pair in subject.Renderers)
        {
            var renderer = pair.Value;
            if (renderer.sharedMesh == null) continue;
            var expected = control.Renderers[pair.Key];
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                float error = Mathf.Abs(renderer.GetBlendShapeWeight(i) - expected.GetBlendShapeWeight(i));
                report.maximumBlendshapeError = Mathf.Max(report.maximumBlendshapeError, error);
                Check(error < 0.001f && Mathf.Abs(renderer.GetBlendShapeWeight(i) - (3f + i % 13)) < 0.001f,
                    "Tail changed expression blendshape " + renderer.sharedMesh.GetBlendShapeName(i));
            }
        }
        for (int i = 0; i < boneLengths.Length; i++)
        {
            float length = Vector3.Distance(chain[i].position, chain[i + 1].position);
            float error = Mathf.Abs(length - boneLengths[i]);
            report.maximumBoneLengthErrorMetres = Mathf.Max(report.maximumBoneLengthErrorMetres, error);
            Check(!float.IsNaN(length) && !float.IsInfinity(length) && error < 0.001f,
                "Tail segment changed length or became non-finite: " + chain[i].name);
        }
        report.invariantFrames++;
    }

    private static void InitializeSkinProbes()
    {
        // Select the same real vertices before authoring the curve. Re-binning a deformed tail
        // would confuse movement of the visible fur with changes in region membership.
        var projections = new Dictionary<TailSkinProbe, float[]>();
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        Vector3 originalDirection = (chain[chain.Length - 1].position - chain[0].position).normalized;
        foreach (var renderer in subject.Renderers.Values)
        {
            Mesh mesh = renderer.sharedMesh;
            if (mesh == null) continue;
            BoneWeight[] weights = mesh.boneWeights;
            if (weights.Length != mesh.vertexCount) continue;
            var tailBones = new HashSet<int>(Enumerable.Range(0, renderer.bones.Length)
                .Where(i => chain.Contains(renderer.bones[i])));
            if (tailBones.Count == 0) continue;
            var probe = new TailSkinProbe
            {
                Renderer = renderer, Baked = new Mesh(),
                VertexRegions = Enumerable.Repeat(-1, mesh.vertexCount).ToArray()
            };
            renderer.BakeMesh(probe.Baked);
            probe.Baked.GetVertices(probe.Vertices);
            var distance = Enumerable.Repeat(float.NaN, mesh.vertexCount).ToArray();
            for (int i = 0; i < weights.Length; i++)
            {
                BoneWeight weight = weights[i];
                float tailWeight = (tailBones.Contains(weight.boneIndex0) ? weight.weight0 : 0f) +
                    (tailBones.Contains(weight.boneIndex1) ? weight.weight1 : 0f) +
                    (tailBones.Contains(weight.boneIndex2) ? weight.weight2 : 0f) +
                    (tailBones.Contains(weight.boneIndex3) ? weight.weight3 : 0f);
                if (tailWeight < 0.75f) continue;
                float projection = Vector3.Dot(renderer.transform.TransformPoint(probe.Vertices[i]) - chain[0].position,
                    originalDirection);
                distance[i] = projection;
                minimum = Mathf.Min(minimum, projection);
                maximum = Mathf.Max(maximum, projection);
            }
            projections.Add(probe, distance);
            skinProbes.Add(probe);
        }
        Check(skinProbes.Count > 0 && maximum - minimum > 0.2f, "Could not identify the actual skinned tail surface");
        report.visibleTailRegionVertexCounts = new int[3];
        report.visibleTailRegionLateralSpansMetres = new float[3];
        foreach (var pair in projections)
        {
            for (int i = 0; i < pair.Value.Length; i++)
            {
                if (float.IsNaN(pair.Value[i])) continue;
                float fraction = (pair.Value[i] - minimum) / (maximum - minimum);
                int region = fraction <= 0.2f ? 0 : fraction >= 0.8f ? 2 :
                    fraction >= 0.35f && fraction <= 0.65f ? 1 : -1;
                pair.Key.VertexRegions[i] = region;
                if (region >= 0) report.visibleTailRegionVertexCounts[region]++;
            }
        }
        Check(report.visibleTailRegionVertexCounts.All(count => count >= 20), "Too few visible tail vertices in a measurement region");
    }

    private static void SampleVisibleTail()
    {
        var centres = new Vector3[3];
        foreach (var probe in skinProbes)
        {
            probe.Renderer.BakeMesh(probe.Baked);
            probe.Baked.GetVertices(probe.Vertices);
            Matrix4x4 toAvatar = subject.Root.transform.worldToLocalMatrix * probe.Renderer.transform.localToWorldMatrix;
            for (int i = 0; i < probe.VertexRegions.Length; i++)
            {
                int region = probe.VertexRegions[i];
                if (region >= 0) centres[region] += toAvatar.MultiplyPoint3x4(probe.Vertices[i]);
            }
        }
        for (int i = 0; i < centres.Length; i++) centres[i] /= report.visibleTailRegionVertexCounts[i];
        if (visibleMinimum == null)
        {
            visibleMinimum = (Vector3[])centres.Clone();
            visibleMaximum = (Vector3[])centres.Clone();
        }
        for (int i = 0; i < centres.Length; i++)
        {
            visibleMinimum[i] = Vector3.Min(visibleMinimum[i], centres[i]);
            visibleMaximum[i] = Vector3.Max(visibleMaximum[i], centres[i]);
            report.visibleTailRegionLateralSpansMetres[i] = visibleMaximum[i].x - visibleMinimum[i].x;
        }
        report.visibleTailSamples++;
    }

    private static void ValidateIdleDistribution()
    {
        float[] joints = report.idleJointLateralSpansMetres;
        float[] visible = report.visibleTailRegionLateralSpansMetres;
        Check(joints.Take(3).All(span => span < 0.01f || span < joints[5] * 0.1f),
            "Idle motion moves the anchored root instead of bending the distal tail: " + string.Join(", ", joints));
        for (int i = 3; i < joints.Length; i++)
            Check(joints[i] > joints[i - 1] + 0.003f,
                "Lateral movement must grow towards the tail end: " + string.Join(", ", joints));
        Check(visible[2] >= 0.12f,
            "Actual visible tail tip must travel at least 12 cm laterally: " + visible[2] + " metres");
        Check(visible[0] < 0.01f || visible[0] < visible[2] * 0.1f,
            "Visible fur at the tail base moves too much: root=" + visible[0] + ", tip=" + visible[2]);
        Check(visible[1] > visible[0] + 0.003f && visible[2] > visible[1] + 0.003f,
            "The visible tail surface must sway more towards its tip: " + string.Join(", ", visible));
        Check(report.visibleTailSamples >= 60, "Too few baked mesh samples to verify two idle cycles");
    }

    private static Vector3 JointLocal(Transform joint) => subject.Root.transform.InverseTransformPoint(joint.position);
    private static Vector3 TipLocal() => JointLocal(chain[chain.Length - 1]);
    private static void Advance() { phase++; phaseStartedAt = Time.time; }

    private static Camera CreateCamera()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
        var result = new GameObject("Tail regression side view").AddComponent<Camera>();
        result.enabled = false;
        result.cullingMask = 1 << 31;
        result.clearFlags = CameraClearFlags.SolidColor;
        result.backgroundColor = new Color(0.82f, 0.85f, 0.88f);
        result.orthographic = true;
        result.orthographicSize = 1.05f;
        result.nearClipPlane = 0.01f;
        result.farClipPlane = 20f;
        var centre = subject.Root.transform.position + Vector3.up * 0.9f;
        result.transform.position = centre + subject.Root.transform.right * 4f;
        result.transform.LookAt(centre);
        for (int i = 0; i < 2; i++)
        {
            var light = new GameObject("Tail regression light " + i).AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = i == 0 ? 1.1f : 0.5f;
            light.cullingMask = 1 << 31;
            light.transform.rotation = Quaternion.Euler(35f, i == 0 ? 120f : 300f, 0f);
        }
        return result;
    }

    private static void PrepareIdleCamera()
    {
        if (camera == null) return;
        Vector3 backward = subject.Root.transform.TransformDirection(backwardLocal).normalized;
        Vector3 centre = subject.Root.transform.position + subject.Root.transform.up * 0.82f + backward * 0.18f;
        camera.gameObject.name = "Tail regression rear three-quarter view";
        camera.orthographicSize = 0.98f;
        camera.transform.position = centre + subject.Root.transform.right * 3.2f + backward * 3.2f + Vector3.up * 0.15f;
        camera.transform.LookAt(centre, subject.Root.transform.up);
        rearCamera = new GameObject("Tail regression rear view").AddComponent<Camera>();
        rearCamera.CopyFrom(camera);
        rearCamera.enabled = false;
        rearCamera.transform.position = centre + backward * 4.5f;
        rearCamera.transform.LookAt(centre, subject.Root.transform.up);
        Directory.CreateDirectory(Path.Combine(Output, "idle-frames"));
        Directory.CreateDirectory(Path.Combine(Output, "idle-rear-frames"));
    }

    private static void Capture(string name, int size = 768, Camera view = null)
    {
        if (view == null) view = camera;
        if (view == null) return;
        var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(size, size, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            view.targetTexture = target;
            view.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            image.Apply(false, false);
            File.WriteAllBytes(Path.Combine(Output, name), image.EncodeToPNG());
            captures.Add(name);
        }
        finally
        {
            view.targetTexture = null;
            RenderTexture.active = previous;
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(target);
        }
    }

    private static string Output => SessionState.GetString(Key + "Output", Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports")));

    private static GameObject LoadAvatarAfterShaders()
    {
        // A new Library can import the VRM while Shader.Find's global registry is still empty,
        // even after shader import artifacts were produced. Load the actual shaders explicitly
        // and retry only the isolated avatar's failed import after the initial refresh completes.
        var shaderAssets = new Dictionary<string, string>
        {
            { "VRM10/MToon10", "Assets/VRM10/MToon10/Shaders/vrmc_materials_mtoon.shader" },
            { "UniGLTF/UniUnlit", "Assets/UniGLTF/UniUnlit/Shaders/UniUnlit.shader" },
            { "Hidden/UniGLTF/StandardMapImporter", "Assets/UniGLTF/Resources/UniGLTF/StandardMapImporter.shader" },
            { "Hidden/UniGLTF/StandardMapExporter", "Assets/UniGLTF/Resources/UniGLTF/StandardMapExporter.shader" },
            { "Hidden/UniGLTF/NormalMapExporter", "Assets/UniGLTF/Resources/UniGLTF/NormalMapExporter.shader" }
        };
        var loaded = new List<Shader>();
        foreach (var entry in shaderAssets)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(entry.Value);
            if (shader == null || Shader.Find(entry.Key) == null)
            {
                AssetDatabase.ImportAsset(entry.Value, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(entry.Value);
            }
            Debug.Log("[NevaTailSpringRegression] Shader " + entry.Key + ": asset=" +
                (shader != null ? shader.name : "MISSING") + ", Shader.Find=" +
                (Shader.Find(entry.Key) != null ? "available" : "MISSING"));
            Check(shader != null && Shader.Find(entry.Key) != null, "Required avatar shader unavailable: " + entry.Key);
            loaded.Add(shader); // Keep the loaded shaders alive through the synchronous VRM import.
        }
        Check(Shader.Find("Standard") != null, "Built-in Standard shader unavailable");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarAsset);
        if (prefab == null)
        {
            Debug.Log("[NevaTailSpringRegression] Retrying isolated NEVA.vrm import after loading shader assets.");
            AssetDatabase.ImportAsset(AvatarAsset, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarAsset);
        }
        GC.KeepAlive(loaded);
        return prefab;
    }

    private static string Argument(string flag)
    {
        var arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, flag);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static void VerifySourceFiles()
    {
        string source = Argument("-tailSourceRoot");
        if (string.IsNullOrEmpty(source)) return;
        foreach (string asset in new[] { AvatarAsset, ControllerAsset, RuntimeSource, MotionAssembly, RegressionSource })
            Check(Hash(asset) == Hash(Path.Combine(source, asset)), "Isolated asset differs from source: " + asset);
    }

    private static void CaptureError(string condition, string stackTrace, LogType type)
    {
        if (!SessionState.GetBool(Key + "Active", false) || finishing) return;
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert ||
            condition.IndexOf("memory leak", StringComparison.OrdinalIgnoreCase) >= 0 ||
            condition.IndexOf("has not been disposed", StringComparison.OrdinalIgnoreCase) >= 0)
            errors.Add(condition + "\n" + stackTrace);
    }

    private static void Finish(Exception exception)
    {
        if (finishing) return;
        finishing = true;
        if (report == null) report = new Report();
        report.status = exception == null ? "passed" : "failed: " + exception.Message;
        report.errorLogs = errors.ToArray();
        report.captures = captures.ToArray();
        report.idleCaptureTimesSeconds = idleCaptureTimes.ToArray();
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "tail-spring-regression.json"), JsonUtility.ToJson(report, true));
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
        Debug.Log("[NevaTailSpringRegression] Exit " + code + "; report: " + Path.Combine(Output, "tail-spring-regression.json"));
        EditorApplication.Exit(code);
    }

    private static void Check(bool condition, string message)
    {
        if (report != null) report.checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
