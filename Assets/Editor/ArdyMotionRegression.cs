using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Exercises the retargeter on imported project avatars in an unsaved preview scene.</summary>
public static class ArdyMotionRegression
{
    private static int checks;
    private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/ardy-motion-preview"));

    [Serializable]
    private sealed class Report
    {
        public string status;
        public int checks;
        public string limitation = "Edit-mode playback on real imported VRM prefabs with a fixed idle baseline; this does not measure chat/Animator/VRM late-update concurrency or live ARDY streaming.";
        public List<AvatarResult> avatars = new List<AvatarResult>();
        public List<SceneComponentAudit> sceneComponents = new List<SceneComponentAudit>();
    }

    [Serializable]
    private sealed class SceneComponentAudit
    {
        public string scene;
        public string sourceAsset;
        public string componentType;
        public string localFileId;
        public bool prefabEnabled;
        public bool effectiveSceneEnabled;
        public string effectiveSceneUpdateType;
    }

    [Serializable]
    private sealed class AvatarResult
    {
        public string asset;
        public int boundBones;
        public float leftWristRiseMetres;
        public float rightWristRiseMetres;
        public float nodExcursionDegrees;
        public float shakeExcursionDegrees;
        public float rootYawErrorDegrees;
        public string[] previews;
    }

    private sealed class Fixture : IDisposable
    {
        public Scene Scene;
        public GameObject Root;
        public Vrm10Instance Vrm;
        public ArdyMotionPlayer Player;
        public Camera Camera;
        public readonly Dictionary<Transform, Quaternion> IdleRotations = new Dictionary<Transform, Quaternion>();
        private readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, Vector3> scales = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<SkinnedMeshRenderer, float[]> expressions = new Dictionary<SkinnedMeshRenderer, float[]>();
        private readonly Dictionary<Transform, Vector3> feet = new Dictionary<Transform, Vector3>();
        private readonly Vector3 rootPosition;
        private readonly Quaternion rootRotation;

        public Fixture(string asset, float yaw)
        {
            Scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                if (prefab == null) throw new InvalidOperationException("Could not load imported VRM prefab: " + asset);
                Root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, Scene);
                Root.transform.SetPositionAndRotation(new Vector3(0.7f, 0.1f, -0.2f), Quaternion.Euler(0f, yaw, 0f));
                Vrm = Root.GetComponentInChildren<Vrm10Instance>(true);
                Check(Vrm != null, "VRM 1.0 instance missing on " + asset);
                Vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
                foreach (var animator in Root.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                Player = Vrm.gameObject.AddComponent<ArdyMotionPlayer>();
                Player.Bind(Vrm);
                Check(Player.BoundBoneCount >= 12, "Too few mapped humanoid bones");
                // Binding reads the imported rest pose. The controlled idle below stands in for Animator output.
                SetArmIdle(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, -1f);
                SetArmIdle(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 1f);
                foreach (var transform in Root.GetComponentsInChildren<Transform>(true))
                {
                    IdleRotations[transform] = transform.localRotation;
                    positions[transform] = transform.localPosition;
                    scales[transform] = transform.localScale;
                    transform.gameObject.layer = 31;
                }
                rootPosition = Root.transform.position;
                rootRotation = Root.transform.rotation;
                foreach (var bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes })
                {
                    var transform = Bone(bone);
                    if (transform != null) feet[transform] = transform.position;
                }
                foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null) continue;
                    int count = renderer.sharedMesh.blendShapeCount;
                    // A nonzero value detects accidental expression resets, not just writes of zero to zero.
                    if (count > 0) renderer.SetBlendShapeWeight(0, 17f);
                    expressions[renderer] = Enumerable.Range(0, count).Select(renderer.GetBlendShapeWeight).ToArray();
                    renderer.updateWhenOffscreen = true;
                }
                CreateCamera();
            }
            catch { Dispose(); throw; }
        }

        private void SetArmIdle(HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone, float side)
        {
            var upper = Bone(upperBone);
            var lower = Bone(lowerBone);
            var hand = Bone(handBone);
            Check(upper != null && lower != null && hand != null, "Avatar is missing an arm bone");
            var down = Root.transform.TransformDirection(new Vector3(side * 0.1f, -1f, 0f)).normalized;
            upper.rotation = Quaternion.FromToRotation(lower.position - upper.position, down) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, down) * lower.rotation;
        }

        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);

        public void Step(float deltaTime)
        {
            Player.RestoreAnimatedPose();
            // Verify removal of the previous overlay before providing this frame's Animator pose.
            foreach (var pair in IdleRotations)
                Check(Quaternion.Angle(pair.Key.localRotation, pair.Value) < 0.12f, "Overlay was not restored before Animator: " + pair.Key.name);
            foreach (var pair in IdleRotations) pair.Key.localRotation = pair.Value;
            Player.Tick(deltaTime);
            CheckProtectedChannels();
        }

        public void StepFor(float seconds)
        {
            int frames = Mathf.RoundToInt(seconds * 60f);
            for (int i = 0; i < frames; i++) Step(1f / 60f);
        }

        public void CheckProtectedChannels()
        {
            Check(Vector3.Distance(Root.transform.position, rootPosition) < 0.00001f, "Root position changed");
            Check(Quaternion.Angle(Root.transform.rotation, rootRotation) < 0.05f, "Root heading changed");
            foreach (var pair in positions)
            {
                Check(Vector3.Distance(pair.Key.localPosition, pair.Value) < 0.00001f, "Bone position/length changed: " + pair.Key.name);
                Check(Vector3.Distance(pair.Key.localScale, scales[pair.Key]) < 0.00001f, "Bone scale changed: " + pair.Key.name);
                var q = pair.Key.localRotation;
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                Check(!float.IsNaN(norm) && !float.IsInfinity(norm) && Mathf.Abs(norm - 1f) < 0.0002f, "Invalid quaternion: " + pair.Key.name);
            }
            foreach (var pair in feet) Check(Vector3.Distance(pair.Key.position, pair.Value) < 0.00001f, "Upper body action moved foot: " + pair.Key.name);
            foreach (var pair in expressions)
                for (int i = 0; i < pair.Value.Length; i++)
                    Check(Mathf.Abs(pair.Key.GetBlendShapeWeight(i) - pair.Value[i]) < 0.0001f, "Body playback changed a blendshape");
        }

        public Dictionary<Transform, Quaternion> Pose() => IdleRotations.Keys.ToDictionary(t => t, t => t.localRotation);

        public void CheckNoJump(Dictionary<Transform, Quaternion> previous, string label)
        {
            foreach (var pair in previous) Check(Quaternion.Angle(pair.Value, pair.Key.localRotation) < 0.12f, label + " changed pose at zero elapsed time: " + pair.Key.name);
        }

        private void CreateCamera()
        {
            var cameraObject = new GameObject("ARDY preview camera");
            SceneManager.MoveGameObjectToScene(cameraObject, Scene);
            Camera = cameraObject.AddComponent<Camera>();
            Camera.scene = Scene;
            Camera.cullingMask = 1 << 31;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.78f, 0.83f, 0.87f);
            Camera.orthographic = true;
            var bounds = new Bounds(Root.transform.position + Vector3.up, Vector3.one * 0.01f);
            foreach (var renderer in Root.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(renderer.bounds);
            Camera.orthographicSize = Mathf.Max(1.15f, bounds.size.y * 0.61f);
            var centre = new Vector3(Root.transform.position.x, bounds.center.y, Root.transform.position.z);
            Camera.transform.position = centre + new Vector3(0f, 0.04f, 4f);
            Camera.transform.LookAt(centre);
            Camera.nearClipPlane = 0.01f;
            Camera.farClipPlane = 20f;
            for (int i = 0; i < 2; i++)
            {
                var lightObject = new GameObject("ARDY preview light " + i);
                SceneManager.MoveGameObjectToScene(lightObject, Scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = i == 0 ? 1.1f : 0.65f;
                light.cullingMask = 1 << 31;
                light.shadows = LightShadows.None;
                light.transform.rotation = Quaternion.Euler(i == 0 ? 30f : 340f, i == 0 ? 200f : 25f, 0f);
            }
        }

        public void Capture(string filename)
        {
            var target = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(768, 768, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            var bakedMeshes = new List<Mesh>();
            var bakedObjects = new List<GameObject>();
            var originalRenderers = new Dictionary<SkinnedMeshRenderer, bool>();
            try
            {
                // Camera.Render inside one edit-mode call does not advance GPU skinning.
                // Bake the current bone transforms explicitly so the evidence shows the sampled pose.
                foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                    var mesh = new Mesh();
                    renderer.BakeMesh(mesh);
                    bakedMeshes.Add(mesh);
                    var baked = new GameObject("ARDY pose snapshot");
                    baked.layer = renderer.gameObject.layer;
                    baked.transform.SetParent(renderer.transform, false);
                    baked.AddComponent<MeshFilter>().sharedMesh = mesh;
                    baked.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                    bakedObjects.Add(baked);
                    originalRenderers.Add(renderer, renderer.enabled);
                    renderer.enabled = false;
                }
                Camera.targetTexture = target;
                Camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0);
                image.Apply(false, false);
                var pixels = image.GetPixels32();
                var background = pixels[0];
                int visible = pixels.Count(p => Math.Abs(p.r - background.r) + Math.Abs(p.g - background.g) + Math.Abs(p.b - background.b) > 30);
                Check(visible > pixels.Length / 200, "Preview image is empty: " + filename);
                File.WriteAllBytes(Path.Combine(Output, filename), image.EncodeToPNG());
            }
            finally
            {
                Camera.targetTexture = null;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
                foreach (var pair in originalRenderers) pair.Key.enabled = pair.Value;
                foreach (var baked in bakedObjects) Object.DestroyImmediate(baked);
                foreach (var mesh in bakedMeshes) Object.DestroyImmediate(mesh);
            }
        }

        public void Dispose()
        {
            if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene);
        }
    }

    [MenuItem("Tools/NeEEvA/Run ARDY Motion Regression")]
    public static void RunInteractive()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Run the isolated regression outside Play mode.");
        checks = 0;
        Directory.CreateDirectory(Output);
        var report = new Report { status = "running" };
        try
        {
            report.sceneComponents = AuditSceneComponents();
            foreach (string asset in new[] { "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm" }) report.avatars.Add(RunAvatar(asset));
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed: " + error.Message; throw; }
        finally
        {
            report.checks = checks;
            File.WriteAllText(Path.Combine(Output, "regression.json"), JsonUtility.ToJson(report, true));
        }
        Debug.Log("[ArdyMotionRegression] Passed " + checks + " checks; report and avatar previews: " + Output);
    }

    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    public static void RenderAnimationBatch()
    {
        try
        {
            checks = 0;
            string directory = Path.Combine(Output, "left-wave-frames");
            Directory.CreateDirectory(directory);
            using (var fixture = new Fixture("Assets/Model/NEVA.vrm", 0f))
            {
                fixture.Player.Play(Clip("left-wave"), ArdyMotionMask.LeftArm);
                // Render all three actual ARDY windows and the stop transition at 20 FPS.
                for (int i = 0; i < 141; i++)
                {
                    fixture.Step(i == 0 ? 0f : 0.05f);
                    fixture.Capture("left-wave-frames/frame-" + i.ToString("D4") + ".png");
                }
                Check(!fixture.Player.IsPlaying, "Rendered motion did not return to idle.");
            }
            Debug.Log("[ArdyMotionRegression] Rendered 141 frames of the real NEVA avatar.");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static AvatarResult RunAvatar(string asset)
    {
        var result = new AvatarResult { asset = asset };
        string name = Path.GetFileNameWithoutExtension(asset);
        var previews = new List<string>();
        using (var fixture = new Fixture(asset, 0f))
        {
            result.boundBones = fixture.Player.BoundBoneCount;
            Action<string> capture = suffix => { string file = name + "-" + suffix + ".png"; fixture.Capture(file); previews.Add(file); };
            capture("idle");
            var baseline = fixture.Pose();
            var rightStart = fixture.Bone(HumanBodyBones.RightHand).position;
            var leftStart = fixture.Bone(HumanBodyBones.LeftHand).position;
            fixture.Player.Play(Clip("left-wave"), ArdyMotionMask.LeftArm);
            fixture.Step(0f);
            fixture.CheckNoJump(baseline, "Start");
            float maxLeftHeight = leftStart.y;
            for (int frame = 1; frame <= 96; frame++)
            {
                fixture.Step(1f / 60f);
                maxLeftHeight = Mathf.Max(maxLeftHeight, fixture.Bone(HumanBodyBones.LeftHand).position.y);
                Check(Vector3.Distance(fixture.Bone(HumanBodyBones.RightHand).position, rightStart) < 0.0001f, "Left-arm mask moved right wrist");
                if (frame == 18) capture("left-wave-030");
                if (frame == 48) capture("left-wave-080");
                if (frame == 84) capture("left-wave-140");
            }
            result.leftWristRiseMetres = maxLeftHeight - leftStart.y;
            Check(result.leftWristRiseMetres > 0.15f, "Left wrist did not rise from idle");

            var beforeSwitch = fixture.Pose();
            fixture.Player.Play(Clip("right-wave"), ArdyMotionMask.RightArm);
            fixture.Step(0f);
            fixture.CheckNoJump(beforeSwitch, "Switch");
            float maxRightHeight = rightStart.y;
            for (int frame = 0; frame < 84; frame++)
            {
                fixture.Step(1f / 60f);
                maxRightHeight = Mathf.Max(maxRightHeight, fixture.Bone(HumanBodyBones.RightHand).position.y);
            }
            result.rightWristRiseMetres = maxRightHeight - rightStart.y;
            Check(result.rightWristRiseMetres > 0.1f, "Right wrist did not rise from idle");
            capture("right-wave");
            var beforeStop = fixture.Pose();
            fixture.Player.Stop();
            // Stop can be requested after LateUpdate; a second same-frame Tick must not capture its own overlay.
            fixture.Player.Tick(0f);
            fixture.CheckNoJump(beforeStop, "Stop");
            fixture.StepFor(0.6f);
            Check(!fixture.Player.IsPlaying, "Player still active after fade-out");
            fixture.CheckNoJump(baseline, "Completed stop");
            capture("stopped");

            result.nodExcursionDegrees = CheckHeadAction(fixture, "nod");
            capture("nod");
            fixture.Player.Stop();
            fixture.StepFor(0.6f);
            result.shakeExcursionDegrees = CheckHeadAction(fixture, "shake-head");
            capture("shake-head");
            fixture.Player.Stop();
            fixture.StepFor(0.6f);
            fixture.Player.Play(Clip("left-wave"), ArdyMotionMask.LeftArm);
            fixture.StepFor(ArdyMotionClip.Parse(Clip("left-wave").text).Duration + 1f);
            Check(!fixture.Player.IsPlaying, "Clip did not complete automatically");
            fixture.CheckNoJump(baseline, "Automatic completion");
        }
        result.rootYawErrorDegrees = CheckRootYaw(asset);
        result.previews = previews.ToArray();
        return result;
    }

    private static float CheckHeadAction(Fixture fixture, string resource)
    {
        var head = fixture.Bone(HumanBodyBones.Head);
        Check(head != null, "Avatar has no head bone");
        var original = head.rotation;
        var left = fixture.Bone(HumanBodyBones.LeftHand).position;
        var right = fixture.Bone(HumanBodyBones.RightHand).position;
        fixture.Player.Play(Clip(resource), ArdyMotionMask.Head);
        float excursion = 0f;
        for (int frame = 0; frame < 84; frame++)
        {
            fixture.Step(1f / 60f);
            excursion = Mathf.Max(excursion, Quaternion.Angle(original, head.rotation));
            Check(Vector3.Distance(fixture.Bone(HumanBodyBones.LeftHand).position, left) < 0.0001f, "Head mask moved left wrist");
            Check(Vector3.Distance(fixture.Bone(HumanBodyBones.RightHand).position, right) < 0.0001f, "Head mask moved right wrist");
        }
        Check(excursion > 2f, resource + " has no visible head movement");
        return excursion;
    }

    private static float CheckRootYaw(string asset)
    {
        float maxError = 0f;
        using (var front = new Fixture(asset, 0f))
        using (var turned = new Fixture(asset, 123f))
        {
            front.Player.Play(Clip("left-wave"), ArdyMotionMask.LeftArm);
            turned.Player.Play(Clip("left-wave"), ArdyMotionMask.LeftArm);
            for (int frame = 0; frame < 90; frame++)
            {
                front.Step(1f / 60f);
                turned.Step(1f / 60f);
                foreach (var bone in new[] { HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.Head })
                {
                    var a = front.Bone(bone);
                    var b = turned.Bone(bone);
                    if (a == null || b == null) continue;
                    var relativeA = Quaternion.Inverse(front.Root.transform.rotation) * a.rotation;
                    var relativeB = Quaternion.Inverse(turned.Root.transform.rotation) * b.rotation;
                    float error = Quaternion.Angle(relativeA, relativeB);
                    maxError = Mathf.Max(maxError, error);
                    Check(error < 0.15f, "Retargeting depends on avatar root yaw: " + bone);
                }
            }
        }
        return maxError;
    }

    private static List<SceneComponentAudit> AuditSceneComponents()
    {
        const string scene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
        const string asset = "Assets/Model/NEVA.vrm";
        const long disabledSourceId = -4297446187249348513L;
        var result = new List<SceneComponentAudit>();
        if (!File.Exists(scene)) return result;
        string yaml = File.ReadAllText(scene);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
        Check(prefab != null, "Missing scene avatar source prefab");
        bool foundDisabled = false;
        foreach (var component in prefab.GetComponentsInChildren<Component>(true))
        {
            if (component == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out string guid, out long localId)) continue;
            if (!(component is Vrm10Instance) && localId != disabledSourceId) continue;
            foundDisabled |= localId == disabledSourceId;
            bool enabled = !(component is Behaviour behaviour) || behaviour.enabled;
            string updateType = component is Vrm10Instance vrm ? ((int)vrm.UpdateType).ToString() : string.Empty;
            string Override(string property, string fallback)
            {
                string pattern = @"- target: \{fileID: " + localId + @", guid: " + Regex.Escape(guid)
                    + @", type: 3\}\s*propertyPath: " + Regex.Escape(property) + @"\s*\r?\n\s*value: ([^\r\n]*)";
                var match = Regex.Match(yaml, pattern);
                return match.Success ? match.Groups[1].Value.Trim() : fallback;
            }
            result.Add(new SceneComponentAudit
            {
                scene = scene,
                sourceAsset = asset,
                componentType = component.GetType().FullName,
                localFileId = localId.ToString(),
                prefabEnabled = enabled,
                effectiveSceneEnabled = Override("m_Enabled", enabled ? "1" : "0") != "0",
                effectiveSceneUpdateType = string.IsNullOrEmpty(updateType) ? string.Empty :
                    ((Vrm10Instance.UpdateTypes)int.Parse(Override("UpdateType", updateType))).ToString()
            });
        }
        Check(foundDisabled, "Could not resolve disabled private-scene source component identity");
        return result;
    }

    private static TextAsset Clip(string resource)
    {
        var clip = Resources.Load<TextAsset>("ARDY/" + resource);
        Check(clip != null, "Missing clip: Resources/ARDY/" + resource);
        return clip;
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
