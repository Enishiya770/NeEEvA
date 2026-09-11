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

/// <summary>Chest-relative gesture invariants and rendered naturalness review on real imported VRM avatars.</summary>
public static class ArdyMotionAnchorRegression
{
    private static int checks;
    private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/ardy-naturalness"));
    private static readonly HumanBodyBones[] Arms = { HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand };
    private static readonly HumanBodyBones[] Head = { HumanBodyBones.Neck, HumanBodyBones.Head };

    [Serializable] private sealed class Report
    {
        public string status;
        public int checks;
        public string limitation = "Deterministic isolated imported VRM poses. Hand clearance uses a wrist point and an estimated face centre; it is not a mesh collision test or a subjective naturalness score.";
        public List<Result> avatars = new List<Result>();
    }
    [Serializable] private sealed class Result
    {
        public string avatar;
        public float sourceCommonYawMaxErrorDegrees;
        public float targetChestYawMaxErrorDegrees;
        public float zeroAdditiveHeadMaxErrorDegrees;
        public float mixedModeSwitchMaxErrorDegrees;
        public float rightHandMinimumChestSideMetres = float.MaxValue;
        public float rightHandMinimumFaceDistanceMetres = float.MaxValue;
        public float nodPeakDegrees;
        public float shakePeakDegrees;
        public float headMaximumDegreesPerSecond;
        public float headEndpointMaxDegrees;
    }
    [Serializable] private sealed class RenderReport
    {
        public string avatar = "Assets/Model/NEVA.vrm";
        public string method = "Actual retargeted VRM bones; SkinnedMeshRenderer.BakeMesh for every PNG, rendered at 20 FPS including return to idle. Fixed idle baseline, no live chat or audio.";
        public List<RenderClip> clips = new List<RenderClip>();
    }
    [Serializable] private sealed class RenderClip
    {
        public string id;
        public string sourceJsonPath;
        public string sourceJsonSha256;
        public string motionText;
        public float fps = 20f;
        public List<Sample> samples = new List<Sample>();
    }
    [Serializable] private sealed class Sample
    {
        public int frame;
        public float seconds;
        public float headAngleDegrees;
        public float rightHandChestSideMetres;
        public float rightHandChestHeightMetres;
        public float rightHandFaceDistanceMetres;
        public float rightHandSpeedMetresPerSecond;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Root;
        public readonly Vrm10Instance Vrm;
        public readonly ArdyMotionPlayer Player;
        public readonly Transform Chest;
        public readonly Dictionary<Transform, Quaternion> Baseline = new Dictionary<Transform, Quaternion>();
        private readonly Vector3 anatomicalRightInChest;
        private Camera camera;

        public Fixture(string asset, float chestYaw = 0f, bool inclinedHead = false)
        {
            Scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                if (prefab == null)
                {
                    // A clean project's first parallel import may encounter VRM materials before shaders are registered.
                    // Load the actual shader assets, then retry the real importer rather than replacing the avatar.
                    foreach (string shaderPath in new[] { "Assets/VRM10/MToon10/Shaders/vrmc_materials_mtoon.shader", "Assets/UniGLTF/UniUnlit/Shaders/UniUnlit.shader" })
                        Check(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath) != null, "Missing actual VRM shader: " + shaderPath);
                    AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                }
                Check(prefab != null, "Missing real VRM prefab: " + asset);
                Root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, Scene);
                Vrm = Root.GetComponent<Vrm10Instance>();
                Check(Vrm != null, "Missing imported VRM instance");
                Vrm.enabled = false;
                foreach (var animator in Root.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                Player = Root.AddComponent<ArdyMotionPlayer>();
                Player.Bind(Vrm);
                Chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest) ?? Bone(HumanBodyBones.Spine);
                Check(Chest != null, "Missing chest anchor");
                anatomicalRightInChest = Chest.InverseTransformDirection(Bone(HumanBodyBones.RightUpperArm).position - Bone(HumanBodyBones.LeftUpperArm).position).normalized;
                SetIdleArm(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, -1f);
                SetIdleArm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 1f);
                Chest.rotation = Root.transform.rotation * Quaternion.Euler(0f, chestYaw, 0f) * Quaternion.Inverse(Root.transform.rotation) * Chest.rotation;
                if (inclinedHead)
                {
                    Bone(HumanBodyBones.Neck).localRotation *= Quaternion.Euler(-4f, 3f, 0f);
                    Bone(HumanBodyBones.Head).localRotation *= Quaternion.Euler(8f, 15f, 2f);
                }
                foreach (var transform in Root.GetComponentsInChildren<Transform>(true))
                {
                    Baseline[transform] = transform.localRotation;
                    transform.gameObject.layer = 31;
                }
            }
            catch { Dispose(); throw; }
        }

        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);
        public Quaternion ChestRelative(HumanBodyBones bone) => Quaternion.Inverse(Chest.rotation) * Bone(bone).rotation;
        public Vector3 RightAxis => Chest.TransformDirection(anatomicalRightInChest);
        public Vector3 FaceCentre
        {
            get
            {
                var a = Bone(HumanBodyBones.LeftEye);
                var b = Bone(HumanBodyBones.RightEye);
                return a != null && b != null ? (a.position + b.position) * 0.5f : Bone(HumanBodyBones.Head).position + Vector3.up * 0.08f;
            }
        }

        private void SetIdleArm(HumanBodyBones upperId, HumanBodyBones lowerId, HumanBodyBones handId, float side)
        {
            var upper = Bone(upperId);
            var lower = Bone(lowerId);
            var hand = Bone(handId);
            var direction = Root.transform.TransformDirection(new Vector3(side * 0.1f, -1f, 0f)).normalized;
            upper.rotation = Quaternion.FromToRotation(lower.position - upper.position, direction) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, direction) * lower.rotation;
        }

        public void Step(float delta)
        {
            Player.RestoreAnimatedPose();
            foreach (var pair in Baseline) pair.Key.localRotation = pair.Value;
            Player.Tick(delta);
        }

        public void Capture(string filename)
        {
            if (camera == null) CreateCamera();
            var baked = new List<(SkinnedMeshRenderer source, GameObject view, Mesh mesh)>();
            var target = new RenderTexture(960, 800, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(960, 800, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                    var mesh = new Mesh();
                    renderer.BakeMesh(mesh);
                    var view = new GameObject("ARDY baked review mesh");
                    view.layer = 31;
                    view.transform.SetParent(renderer.transform, false);
                    view.AddComponent<MeshFilter>().sharedMesh = mesh;
                    view.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                    baked.Add((renderer, view, mesh));
                    renderer.enabled = false;
                }
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 960, 800), 0, 0);
                image.Apply(false, false);
                File.WriteAllBytes(filename, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                foreach (var item in baked) { item.source.enabled = true; Object.DestroyImmediate(item.view); Object.DestroyImmediate(item.mesh); }
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
            }
        }

        private void CreateCamera()
        {
            var obj = new GameObject("ARDY naturalness camera");
            SceneManager.MoveGameObjectToScene(obj, Scene);
            camera = obj.AddComponent<Camera>();
            camera.scene = Scene;
            camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.78f, 0.83f, 0.87f);
            camera.orthographic = true;
            camera.orthographicSize = 0.60f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 20f;
            camera.transform.position = new Vector3(0f, 1.4f, 4f);
            camera.transform.LookAt(new Vector3(0f, 1.4f, 0f));
            for (int i = 0; i < 2; i++)
            {
                var lightObject = new GameObject("ARDY review light " + i);
                SceneManager.MoveGameObjectToScene(lightObject, Scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.cullingMask = 1 << 31;
                light.intensity = i == 0 ? 0.7f : 0.25f;
                light.transform.rotation = Quaternion.Euler(i == 0 ? 30f : 340f, i == 0 ? 200f : 25f, 0f);
            }
        }

        public void Dispose() { if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene); }
    }

    [MenuItem("Tools/NeEEvA/Run ARDY Chest Anchor Regression")]
    public static void RunInteractive()
    {
        checks = 0;
        Directory.CreateDirectory(Output);
        var report = new Report { status = "running" };
        try
        {
            foreach (string asset in new[] { "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm" })
            {
                var result = new Result { avatar = asset };
                report.avatars.Add(result);
                CheckAnchorInvariants(asset, result);
                CheckZeroAdditive(asset, result);
                CheckMixedModeSwitches(asset, result);
                CheckHead(asset, "nod", result);
                CheckHead(asset, "shake-head", result);
                CheckRightHand(asset, result);
            }
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed: " + error.Message; throw; }
        finally
        {
            report.checks = checks;
            File.WriteAllText(Path.Combine(Output, "anchor-regression.json"), JsonUtility.ToJson(report, true));
        }
        Debug.Log("[ArdyMotionAnchorRegression] Passed " + checks + " checks");
    }

    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    /// <summary>Prepare both real avatar imports in a clean project, then run the unchanged existing suite.</summary>
    public static void RunExistingEditBatch()
    {
        try
        {
            foreach (string shaderPath in new[] { "Assets/VRM10/MToon10/Shaders/vrmc_materials_mtoon.shader", "Assets/UniGLTF/UniUnlit/Shaders/UniUnlit.shader" })
                Check(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath) != null, "Missing actual VRM shader: " + shaderPath);
            foreach (string asset in new[] { "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm" })
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(asset) == null)
                    AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Check(AssetDatabase.LoadAssetAtPath<GameObject>(asset) != null, "Real VRM import still failed: " + asset);
            }
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); return; }
        ArdyMotionRegression.RunBatch();
    }

    private static void CheckAnchorInvariants(string asset, Result result)
    {
        foreach (var item in new[] { ("left-wave", ArdyMotionMask.LeftArm), ("right-wave", ArdyMotionMask.RightArm), ("nod", ArdyMotionMask.Head) })
        {
            var original = Clip(item.Item1);
            var clone = ArdyMotionClip.Parse(original.text);
            Quaternion commonYaw = Quaternion.Euler(0f, 57f, 0f);
            foreach (var frame in clone.frames)
                for (int i = 0; i < frame.globalRotations.Length; i++) frame.globalRotations[i] = commonYaw * frame.globalRotations[i];
            var turnedSource = new TextAsset(JsonUtility.ToJson(clone));
            try
            {
                using (var a = new Fixture(asset))
                using (var b = new Fixture(asset))
                using (var turnedChest = new Fixture(asset, 34f))
                {
                    a.Player.Play(original, item.Item2);
                    b.Player.Play(turnedSource, item.Item2);
                    turnedChest.Player.Play(original, item.Item2);
                    var bones = item.Item2 == ArdyMotionMask.Head ? Head : Arms.Where(x => x.ToString().StartsWith(item.Item2 == ArdyMotionMask.LeftArm ? "Left" : "Right")).ToArray();
                    int frames = Mathf.RoundToInt(clone.Duration * 60f);
                    for (int frame = 0; frame < frames; frame++)
                    {
                        a.Step(1f / 60f); b.Step(1f / 60f); turnedChest.Step(1f / 60f);
                        foreach (var bone in bones)
                        {
                            if (a.Bone(bone) == null) continue;
                            float sourceError = Quaternion.Angle(a.ChestRelative(bone), b.ChestRelative(bone));
                            float chestError = Quaternion.Angle(a.ChestRelative(bone), turnedChest.ChestRelative(bone));
                            result.sourceCommonYawMaxErrorDegrees = Mathf.Max(result.sourceCommonYawMaxErrorDegrees, sourceError);
                            result.targetChestYawMaxErrorDegrees = Mathf.Max(result.targetChestYawMaxErrorDegrees, chestError);
                            Check(sourceError < 0.2f, item.Item1 + " retained a shared source body yaw in " + bone);
                            Check(chestError < 0.2f, item.Item1 + " failed to follow the current animated chest at " + bone);
                        }
                    }
                }
            }
            finally { Object.DestroyImmediate(turnedSource); }
        }
    }

    private static void CheckZeroAdditive(string asset, Result result)
    {
        var clip = ArdyMotionClip.Parse(Clip("nod").text);
        Check(clip.source.rotationApplication == "additive-local", "Head resource must explicitly use additive-local rotations");
        foreach (var frame in clip.frames) frame.globalRotations = (Quaternion[])clip.restGlobalRotations.Clone();
        var zero = new TextAsset(JsonUtility.ToJson(clip));
        try
        {
            using (var fixture = new Fixture(asset, 29f, true))
            {
                fixture.Player.Play(zero, ArdyMotionMask.Head);
                var head = fixture.Bone(HumanBodyBones.Head);
                var initialBaseline = fixture.Baseline[head];
                int stopFrame = Mathf.Min(45, Mathf.FloorToInt(clip.Duration * 60f * 0.65f));
                for (int i = 0; i < stopFrame + 35; i++)
                {
                    fixture.Baseline[head] = initialBaseline * Quaternion.Euler(2f * Mathf.Sin(i * 0.11f), 4f * Mathf.Sin(i * 0.13f), 0f);
                    if (i == stopFrame)
                    {
                        Check(fixture.Player.IsPlaying, "Zero-additive clip ended before the explicit fade-out test");
                        fixture.Player.Stop();
                    }
                    fixture.Step(1f / 60f);
                    foreach (var bone in Head)
                    {
                        float error = Quaternion.Angle(fixture.Bone(bone).localRotation, fixture.Baseline[fixture.Bone(bone)]);
                        result.zeroAdditiveHeadMaxErrorDegrees = Mathf.Max(result.zeroAdditiveHeadMaxErrorDegrees, error);
                        Check(error < 0.15f, "Zero head offset changed the current animated baseline");
                    }
                }
            }
        }
        finally { Object.DestroyImmediate(zero); }
    }

    private static void CheckRightHand(string asset, Result result)
    {
        using (var fixture = new Fixture(asset, 17f))
        {
            var clip = Clip("right-wave");
            fixture.Player.Play(clip, ArdyMotionMask.RightArm);
            int frames = Mathf.CeilToInt((ArdyMotionClip.Parse(clip.text).Duration + 0.5f) * 60f);
            for (int i = 0; i < frames; i++)
            {
                fixture.Step(1f / 60f);
                var hand = fixture.Bone(HumanBodyBones.RightHand).position;
                float side = Vector3.Dot(fixture.RightAxis, hand - fixture.Chest.position);
                result.rightHandMinimumChestSideMetres = Mathf.Min(result.rightHandMinimumChestSideMetres, side);
                result.rightHandMinimumFaceDistanceMetres = Mathf.Min(result.rightHandMinimumFaceDistanceMetres, Vector3.Distance(hand, fixture.FaceCentre));
            }
            Check(result.rightHandMinimumChestSideMetres >= -0.005f,
                "Right wrist crossed the anatomical chest midline; minimum side=" + result.rightHandMinimumChestSideMetres
                + ", minimum face-point distance=" + result.rightHandMinimumFaceDistanceMetres);
        }
    }

    private static void CheckMixedModeSwitches(string asset, Result result)
    {
        foreach (var item in new[]
        {
            ("nod", ArdyMotionMask.Head, "right-wave", ArdyMotionMask.RightArm),
            ("right-wave", ArdyMotionMask.RightArm, "nod", ArdyMotionMask.Head),
            ("right-wave", ArdyMotionMask.UpperBody, "nod", ArdyMotionMask.Head)
        })
        {
            using (var fixture = new Fixture(asset, 13f, true))
            {
                fixture.Player.Play(Clip(item.Item1), item.Item2);
                for (int i = 0; i < 42; i++) fixture.Step(1f / 60f);
                var before = fixture.Baseline.Keys.ToDictionary(t => t, t => t.localRotation);
                fixture.Player.Play(Clip(item.Item3), item.Item4);
                fixture.Step(0f);
                foreach (var pair in before)
                {
                    float error = Quaternion.Angle(pair.Value, pair.Key.localRotation);
                    result.mixedModeSwitchMaxErrorDegrees = Mathf.Max(result.mixedModeSwitchMaxErrorDegrees, error);
                    Check(error < 0.15f, item.Item2 + " to " + item.Item4 + " jumped at zero elapsed time: " + pair.Key.name);
                }
                for (int i = 0; i < 42; i++) fixture.Step(1f / 60f);
                fixture.Player.Stop();
                for (int i = 0; i < 30; i++) fixture.Step(1f / 60f);
                Check(!fixture.Player.IsPlaying, "Mixed-mode stop did not complete");
                foreach (var pair in fixture.Baseline)
                    Check(Quaternion.Angle(pair.Value, pair.Key.localRotation) < 0.15f, "Mixed-mode stop did not return to the current baseline");
            }
        }
    }

    private static void CheckHead(string asset, string id, Result result)
    {
        var clip = ArdyMotionClip.Parse(Clip(id).text);
        Check(clip.source.rotationApplication == "additive-local", id + " is not an additive conversational gesture");
        foreach (string name in new[] { "Neck", "Head" })
        {
            int joint = Array.IndexOf(clip.jointNames, name);
            Check(joint >= 0, "Missing source head joint");
            foreach (float time in new[] { 0f, clip.Duration })
            {
                float angle = Quaternion.Angle(Quaternion.identity, clip.SampleLocalDelta(joint, time));
                result.headEndpointMaxDegrees = Mathf.Max(result.headEndpointMaxDegrees, angle);
                Check(angle < 0.15f, id + " endpoints do not return to zero additive offset");
            }
        }
        using (var fixture = new Fixture(asset, 12f, true))
        {
            var head = fixture.Bone(HumanBodyBones.Head);
            Quaternion start = head.rotation, previous = start;
            float peak = 0f;
            fixture.Player.Play(Clip(id), ArdyMotionMask.Head);
            int frames = Mathf.CeilToInt((clip.Duration + 0.5f) * 60f);
            for (int i = 0; i < frames; i++)
            {
                fixture.Step(1f / 60f);
                peak = Mathf.Max(peak, Quaternion.Angle(start, head.rotation));
                float speed = Quaternion.Angle(previous, head.rotation) * 60f;
                result.headMaximumDegreesPerSecond = Mathf.Max(result.headMaximumDegreesPerSecond, speed);
                previous = head.rotation;
                Check(speed < 140f, id + " contains an abrupt head step");
            }
            Check(peak > 2f && peak < 20f, id + " excursion should be visible and small: " + peak);
            Check(Quaternion.Angle(start, head.rotation) < 0.15f, id + " did not return to the inclined animated baseline");
            if (id == "nod") result.nodPeakDegrees = peak; else result.shakePeakDegrees = peak;
        }
    }

    public static void RenderBatch()
    {
        try
        {
            Directory.CreateDirectory(Output);
            var report = new RenderReport();
            foreach (string id in new[] { "right-wave", "nod", "shake-head" })
            {
                string directory = Path.Combine(Output, id);
                var source = Clip(id);
                RenderOne(report, id, source, AssetDatabase.GetAssetPath(source), directory,
                    id == "right-wave" ? ArdyMotionMask.RightArm : ArdyMotionMask.Head);
            }
            File.WriteAllText(Path.Combine(Output, "render-manifest.json"), JsonUtility.ToJson(report, true));
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    /// <summary>Render explicit candidate JSONs without replacing any Resources asset.</summary>
    public static void RenderCandidateBatch() => ProcessCandidates(true);

    /// <summary>Rebuild candidate metadata without changing previously reviewed PNG evidence.</summary>
    public static void MeasureCandidateBatch() => ProcessCandidates(false);

    private static void ProcessCandidates(bool captureFrames)
    {
        try
        {
            string explicitPaths = Environment.GetEnvironmentVariable("NEEEVA_ARDY_CANDIDATES");
            string directory = Environment.GetEnvironmentVariable("NEEEVA_ARDY_CANDIDATE_DIR");
            string[] paths = !string.IsNullOrWhiteSpace(explicitPaths)
                ? explicitPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                : !string.IsNullOrWhiteSpace(directory) ? Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
            Check(paths.Length >= 1 && paths.Length <= 8, "Supply 1–8 candidate JSON paths in NEEEVA_ARDY_CANDIDATES (semicolon separated), or NEEEVA_ARDY_CANDIDATE_DIR.");
            string manifestPath = Path.Combine(Output, "candidate-render-manifest.json");
            var report = File.Exists(manifestPath) ? JsonUtility.FromJson<RenderReport>(File.ReadAllText(manifestPath)) : new RenderReport();
            if (report.clips == null) report.clips = new List<RenderClip>();
            var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string suppliedPath in paths)
            {
                string path = Path.GetFullPath(suppliedPath.Trim());
                Check(File.Exists(path), "Missing candidate JSON: " + path);
                string id = string.Concat(Path.GetFileNameWithoutExtension(path).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
                Check(!string.IsNullOrEmpty(id) && outputNames.Add(id), "Candidate filenames must have distinct output names");
                var source = new TextAsset(File.ReadAllText(path));
                try { RenderOne(report, id, source, path, Path.Combine(Output, "candidates", id), ArdyMotionMask.RightArm, captureFrames); }
                finally { Object.DestroyImmediate(source); }
            }
            Directory.CreateDirectory(Output);
            File.WriteAllText(manifestPath, JsonUtility.ToJson(report, true));
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void RenderOne(RenderReport report, string id, TextAsset source, string sourcePath, string directory, ArdyMotionMask mask, bool captureFrames = true)
    {
        Directory.CreateDirectory(directory);
        var parsed = ArdyMotionClip.Parse(source.text);
        var rendered = new RenderClip { id = id, sourceJsonPath = sourcePath, motionText = parsed.text };
        using (var sha = SHA256.Create())
            rendered.sourceJsonSha256 = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(source.text))).Replace("-", "").ToLowerInvariant();
        report.clips.RemoveAll(existing => existing.id == id);
        report.clips.Add(rendered);
        using (var fixture = new Fixture(report.avatar))
        {
            Quaternion initialHead = fixture.Bone(HumanBodyBones.Head).rotation;
            Vector3 previousHand = fixture.Bone(HumanBodyBones.RightHand).position;
            fixture.Player.Play(source, mask);
            int frames = Mathf.CeilToInt((parsed.Duration + 0.6f) * 20f) + 1;
            for (int frame = 0; frame < frames; frame++)
            {
                fixture.Step(frame == 0 ? 0f : 0.05f);
                var hand = fixture.Bone(HumanBodyBones.RightHand).position;
                rendered.samples.Add(new Sample
                {
                    frame = frame, seconds = frame / 20f,
                    headAngleDegrees = Quaternion.Angle(initialHead, fixture.Bone(HumanBodyBones.Head).rotation),
                    rightHandChestSideMetres = Vector3.Dot(fixture.RightAxis, hand - fixture.Chest.position),
                    rightHandChestHeightMetres = hand.y - fixture.Chest.position.y,
                    rightHandFaceDistanceMetres = Vector3.Distance(hand, fixture.FaceCentre),
                    rightHandSpeedMetresPerSecond = frame == 0 ? 0f : Vector3.Distance(hand, previousHand) * 20f
                });
                previousHand = hand;
                if (captureFrames) fixture.Capture(Path.Combine(directory, "frame-" + frame.ToString("D4") + ".png"));
            }
        }
        File.WriteAllText(Path.Combine(directory, "render-metadata.json"), JsonUtility.ToJson(rendered, true));
        Debug.Log("[ArdyMotionAnchorRegression] " + (captureFrames ? "Rendered " : "Measured ") + id + ": " + rendered.samples.Count + " frames");
    }

    private static TextAsset Clip(string id)
    {
        var asset = Resources.Load<TextAsset>("ARDY/" + id);
        Check(asset != null, "Missing ARDY resource " + id);
        return asset;
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
}
