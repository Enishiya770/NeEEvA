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

/// <summary>Paired rendered diagnostics only; never changes native input clips or accepted runtime assets.</summary>
public static class ArdyNativeMotionRender
{
    [Serializable] private sealed class Input { public string baselinePath, outputDirectory; public Entry[] clips; public bool originalMappingOnly; }
    [Serializable] private sealed class Entry { public string id, path; }
    [Serializable] private sealed class Result
    {
        public string id, inputPath, inputSha256, text, mapping, framesDirectory;
        public string baseline = "Frozen actual captured Animator hugshoulder_00 pose; identical for both mappings";
        public string rendering = "Actual VRM joints retargeted with production Player, every frame SkinnedMeshRenderer.BakeMesh, 20 FPS including fade out";
        public string limitation = "Wrist heights/face point clearance and torso angles are diagnostics, not mesh collision or subjective naturalness ratings";
        public List<Sample> samples = new List<Sample>();
    }
    [Serializable] private sealed class Sample
    {
        public int frame;
        public float seconds, sourceSeconds, sourceHipsTiltDegrees, sourceChestTiltDegrees, sourceChestRelativeHipsTiltDegrees;
        public float targetTorsoTiltDegrees, targetHeadTiltDegrees, leftWristAboveShoulder, rightWristAboveShoulder;
        public float leftWristAboveHead, rightWristAboveHead, handsSeparation, leftWristFaceDistance, rightWristFaceDistance;
    }
    [Serializable] private sealed class Manifest
    {
        public string unityVersion, playerSourceSha256, capturedPoseSha256;
        public Vector3 cameraCenter;
        public float cameraOrthographicSize;
        public List<Result> clips = new List<Result>();
    }
    [Serializable] private sealed class FramePose { public string image, inputSha256; public int frame; public List<BonePose> bones = new List<BonePose>(); }
    [Serializable] private sealed class BonePose { public string name, path; public Vector3 localPosition, worldPosition; public Quaternion localRotation, worldRotation; }

    private sealed class Fixture : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Root;
        public readonly Vrm10Instance Vrm;
        public readonly ArdyMotionPlayer Player;
        public readonly Dictionary<Transform, Quaternion> Baseline = new Dictionary<Transform, Quaternion>();
        public readonly Quaternion HipsDelta, HeadRest;
        private Camera camera;

        public Fixture(ArdyNativeMotionDiagnostics.PoseCapture captured)
        {
            Scene = EditorSceneManager.NewPreviewScene();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(captured.avatar);
            if (prefab == null) throw new InvalidOperationException("Missing captured avatar");
            Root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, Scene);
            Vrm = Root.GetComponentInChildren<Vrm10Instance>(true);
            Vrm.enabled = false;
            foreach (var animator in Root.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
            HeadRest = Quaternion.Inverse(Root.transform.rotation) * Bone(HumanBodyBones.Head).rotation;
            Player = Vrm.gameObject.AddComponent<ArdyMotionPlayer>();
            Player.Bind(Vrm);
            foreach (var item in captured.bones)
            {
                Transform target = item.path.Length == 0 ? Root.transform : Root.transform.Find(item.path);
                if (target == null) throw new InvalidOperationException("Captured pose bone is missing: " + item.path);
                target.localPosition = item.position;
                target.localRotation = item.rotation;
                target.localScale = item.scale;
            }
            HipsDelta = Quaternion.Inverse(Root.transform.rotation) * Bone(HumanBodyBones.Hips).rotation * Quaternion.Inverse(captured.hipsRestInRoot);
            foreach (var transform in Root.GetComponentsInChildren<Transform>(true))
            { Baseline[transform] = transform.localRotation; transform.gameObject.layer = 31; }
            var cameraObject = new GameObject("Native comparison camera");
            SceneManager.MoveGameObjectToScene(cameraObject, Scene);
            camera = cameraObject.AddComponent<Camera>();
            camera.scene = Scene;
            camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.78f, .83f, .87f);
            camera.orthographic = true;
            camera.orthographicSize = 1.05f;
            camera.nearClipPlane = .01f; camera.farClipPlane = 20;
            camera.transform.position = new Vector3(0, 1.5f, 4);
            camera.transform.LookAt(new Vector3(0, 1.5f, 0));
            for (int i = 0; i < 2; i++)
            {
                var lightObject = new GameObject("Native comparison light");
                SceneManager.MoveGameObjectToScene(lightObject, Scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional; light.cullingMask = 1 << 31;
                light.intensity = i == 0 ? .7f : .25f;
                light.transform.rotation = Quaternion.Euler(i == 0 ? 30 : 340, i == 0 ? 200 : 25, 0);
            }
        }
        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);
        public Bounds FramingBounds()
        {
            var bounds = new Bounds(Bone(HumanBodyBones.Hips).position, Vector3.zero);
            foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm })
                bounds.Encapsulate(Bone(bone).position);
            foreach (var name in new[] { HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal,
                HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal })
            {
                var finger = Bone(name);
                if (finger != null) bounds.Encapsulate(finger.position);
            }
            // Include the actual head/ears and waist instead of centering on a guessed avatar height.
            bounds.Encapsulate(Bone(HumanBodyBones.Head).position + Vector3.up * .20f);
            bounds.Encapsulate(Bone(HumanBodyBones.Hips).position - Vector3.up * .10f);
            return bounds;
        }
        public void FrameCamera(Vector3 center, float size)
        {
            camera.orthographicSize = size;
            camera.transform.position = new Vector3(center.x, center.y, 4);
            camera.transform.LookAt(new Vector3(center.x, center.y, 0));
        }
        public void SavePose(string path, int frame, string inputSha)
        {
            var pose = new FramePose { image = Path.GetFileNameWithoutExtension(path).Replace(".pose", "") + ".png", frame = frame, inputSha256 = inputSha };
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = Bone((HumanBodyBones)i);
                if (bone == null) continue;
                pose.bones.Add(new BonePose { name = ((HumanBodyBones)i).ToString(), path = AnimationUtility.CalculateTransformPath(bone, Root.transform),
                    localPosition = bone.localPosition, localRotation = bone.localRotation, worldPosition = bone.position, worldRotation = bone.rotation });
            }
            File.WriteAllText(path, JsonUtility.ToJson(pose, true));
        }
        public void Step(float delta)
        {
            Player.RestoreAnimatedPose();
            foreach (var pair in Baseline) pair.Key.localRotation = pair.Value;
            Player.Tick(delta);
        }
        public Sample Measure(int frame, float seconds, ArdyMotionClip original)
        {
            // Tick samples its current time before advancing the playback clock.
            float sourceSeconds = Mathf.Max(0, seconds - .05f);
            int index = Mathf.Clamp(Mathf.RoundToInt(sourceSeconds * original.fps), 0, original.frames.Length - 1);
            Quaternion[] source = original.frames[index].globalRotations;
            Vector3 left = Bone(HumanBodyBones.LeftHand).position, right = Bone(HumanBodyBones.RightHand).position;
            Vector3 head = Bone(HumanBodyBones.Head).position, hip = Bone(HumanBodyBones.Hips).position;
            Vector3 ls = Bone(HumanBodyBones.LeftUpperArm).position, rs = Bone(HumanBodyBones.RightUpperArm).position;
            var eyeA = Bone(HumanBodyBones.LeftEye); var eyeB = Bone(HumanBodyBones.RightEye);
            Vector3 face = eyeA != null && eyeB != null ? (eyeA.position + eyeB.position) * .5f : head;
            Quaternion headDelta = Quaternion.Inverse(Root.transform.rotation) * Bone(HumanBodyBones.Head).rotation * Quaternion.Inverse(HeadRest);
            return new Sample {
                frame = frame, seconds = seconds, sourceSeconds = sourceSeconds,
                sourceHipsTiltDegrees = Vector3.Angle(source[0] * Vector3.up, Vector3.up),
                sourceChestTiltDegrees = Vector3.Angle(source[4] * Vector3.up, Vector3.up),
                sourceChestRelativeHipsTiltDegrees = Vector3.Angle(Quaternion.Inverse(source[0]) * source[4] * Vector3.up, Vector3.up),
                targetTorsoTiltDegrees = Vector3.Angle((ls + rs) * .5f - hip, Root.transform.up),
                targetHeadTiltDegrees = Vector3.Angle(headDelta * Vector3.up, Vector3.up),
                leftWristAboveShoulder = left.y - ls.y, rightWristAboveShoulder = right.y - rs.y,
                leftWristAboveHead = left.y - head.y, rightWristAboveHead = right.y - head.y,
                handsSeparation = Vector3.Distance(left, right), leftWristFaceDistance = Vector3.Distance(left, face), rightWristFaceDistance = Vector3.Distance(right, face)
            };
        }
        public void Capture(string path)
        {
            var baked = new List<(SkinnedMeshRenderer renderer, GameObject view, Mesh mesh)>();
            var renderTexture = new RenderTexture(960, 900, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(960, 900, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                    var mesh = new Mesh(); renderer.BakeMesh(mesh);
                    var view = new GameObject("Native baked motion mesh"); view.layer = 31;
                    view.transform.SetParent(renderer.transform, false);
                    view.AddComponent<MeshFilter>().sharedMesh = mesh;
                    view.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                    baked.Add((renderer, view, mesh)); renderer.enabled = false;
                }
                camera.targetTexture = renderTexture; camera.Render(); RenderTexture.active = renderTexture;
                image.ReadPixels(new Rect(0, 0, 960, 900), 0, 0); image.Apply(false, false);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = previous;
                foreach (var entry in baked) { entry.renderer.enabled = true; Object.DestroyImmediate(entry.view); Object.DestroyImmediate(entry.mesh); }
                Object.DestroyImmediate(image); Object.DestroyImmediate(renderTexture);
            }
        }
        public void Dispose() { if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene); }
    }

    public static void RenderBatch()
    {
        try
        {
            if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Use only isolated validation project");
            string[] args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-ardyNativeManifest");
            if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("Provide -ardyNativeManifest");
            var input = JsonUtility.FromJson<Input>(File.ReadAllText(args[at + 1]));
            var captured = JsonUtility.FromJson<ArdyNativeMotionDiagnostics.PoseCapture>(File.ReadAllText(input.baselinePath));
            var manifest = new Manifest { unityVersion = Application.unityVersion,
                playerSourceSha256 = Sha(Path.Combine(Application.dataPath, "AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs")),
                capturedPoseSha256 = Sha(input.baselinePath) };
            Directory.CreateDirectory(input.outputDirectory);
            // A first pass measures the same native trajectories to choose ONE fixed camera for all panels.
            // This changes framing only; the input clips and final pose evaluation remain unchanged.
            Bounds framing = default; bool haveBounds = false;
            foreach (Entry entry in input.clips)
            using (var fixture = new Fixture(captured))
            {
                var original = ArdyMotionClip.Parse(File.ReadAllText(entry.path));
                var asset = new TextAsset(JsonUtility.ToJson(original));
                try
                {
                    fixture.Player.Play(asset, ArdyMotionMask.UpperBody);
                    for (int i = 0; i < original.frames.Length + 10; i++)
                    {
                        fixture.Step(i == 0 ? 0 : .05f);
                        var current = fixture.FramingBounds();
                        if (!haveBounds) { framing = current; haveBounds = true; } else framing.Encapsulate(current);
                    }
                }
                finally { Object.DestroyImmediate(asset); }
            }
            manifest.cameraCenter = framing.center;
            manifest.cameraOrthographicSize = Mathf.Max(framing.extents.y, framing.extents.x / (960f / 900f)) * 1.12f;
            foreach (Entry entry in input.clips)
            foreach (bool hipsAnchor in input.originalMappingOnly ? new[] { false } : new[] { false, true })
            using (var fixture = new Fixture(captured))
            {
                fixture.FrameCamera(manifest.cameraCenter, manifest.cameraOrthographicSize);
                string originalText = File.ReadAllText(entry.path);
                var original = ArdyMotionClip.Parse(originalText);
                var mapped = ArdyMotionClip.Parse(originalText);
                string mapping = hipsAnchor ? "hips-anchor-candidate" : "current-root-global";
                if (hipsAnchor)
                {
                    // This encodes only the proposed mapping transform for a paired diagnostic.
                    // Every native relative joint rotation remains unchanged; no pose is invented.
                    foreach (var frame in mapped.frames)
                    {
                        Quaternion correction = fixture.HipsDelta * Quaternion.Inverse(frame.globalRotations[0]);
                        for (int i = 1; i < 19; i++) frame.globalRotations[i] = (correction * frame.globalRotations[i]).normalized;
                    }
                }
                string directory = Path.Combine(input.outputDirectory, entry.id + "-" + mapping);
                Directory.CreateDirectory(directory);
                var result = new Result { id = entry.id, inputPath = entry.path, inputSha256 = Sha(entry.path), text = original.text, mapping = mapping, framesDirectory = directory };
                var asset = new TextAsset(JsonUtility.ToJson(mapped));
                try
                {
                    fixture.Player.Play(asset, ArdyMotionMask.UpperBody);
                    int total = original.frames.Length + 10;
                    for (int i = 0; i < total; i++)
                    {
                        fixture.Step(i == 0 ? 0 : .05f);
                        result.samples.Add(fixture.Measure(i, i * .05f, original));
                        fixture.SavePose(Path.Combine(directory, "frame-" + i.ToString("D4") + ".pose.json"), i, result.inputSha256);
                        fixture.Capture(Path.Combine(directory, "frame-" + i.ToString("D4") + ".png"));
                    }
                }
                finally { Object.DestroyImmediate(asset); }
                File.WriteAllText(Path.Combine(directory, "metrics.json"), JsonUtility.ToJson(result, true));
                manifest.clips.Add(result);
                if (Sha(entry.path) != result.inputSha256) throw new InvalidOperationException("Native input changed during rendering");
                Debug.Log("[ArdyNativeRender] Rendered " + entry.id + " / " + mapping);
            }
            File.WriteAllText(Path.Combine(input.outputDirectory, "render-manifest.json"), JsonUtility.ToJson(manifest, true));
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    private static string Sha(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
