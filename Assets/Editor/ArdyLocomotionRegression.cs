using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Isolated real imported-avatar geometry checks for the locomotion player. Synthetic source
/// trajectories test coordinates and ownership; they never establish native ARDY motion quality.
/// </summary>
public static class ArdyLocomotionRegression
{
    private static int checks;
    private static readonly string[] SourceNames = {
        "Hips", "Spine", "Spine1", "Spine2", "Spine3", "Neck", "Head", "RightShoulder",
        "RightArm", "RightForeArm", "RightHand", "RightHandEnd", "RightHandThumb1",
        "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandEnd", "LeftHandThumb1",
        "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase" };
    private static readonly int[] SourceParents = { -1, 0, 1, 2, 3, 4, 5, 4, 7, 8, 9, 10, 10, 4, 13, 14, 15, 16, 16, 0, 19, 20, 21, 0, 23, 24, 25 };

    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, playerSha256, harnessSha256, arrivalTimingSha256;
        public string execution = "Edit-mode SampleAt with explicit Stop/RestorePreview calls on actual imported Humanoid transforms in disposable preview scenes; no Animator/VRM player loop or automatic lifecycle dispatch.";
        public string scope = "Synthetic Core27 rotations and analytically specified Unity-coordinate pelvis trajectories verify retargeting and ownership only.";
        public string limitations = "No native ARDY inference, collision/navigation, network, chat, live scheduling, foot IK or visual/naturalness acceptance. Enabled-state restoration does not prove simultaneous Animator/VRM execution.";
        public int checks;
        public List<FixtureResult> fixtures = new List<FixtureResult>();
        public List<ArrivalTimingResult> arrivalTimingFixtures = new List<ArrivalTimingResult>();
    }
    [Serializable] private sealed class ArrivalTimingResult
    {
        public string name;
        public int rawFrames, terminalHoldStartFrame, selectedEndFrame;
        public float rawLastSampleSeconds, selectedEndSeconds, plannedArrivalSeconds, skippedTailSeconds;
    }
    [Serializable] private sealed class FixtureResult
    {
        public string avatar, avatarSha256;
        public float avatarScale, startYaw, motionScale, rootPositionErrorMetres, rootYawErrorDegrees, hipsHeightErrorMetres, legRotationErrorDegrees;
        public int mappedBones;
    }
    [Serializable] private sealed class NativeReport
    {
        public string status = "running", error, unityVersion, inputPath, inputSha256, playerSha256, harnessSha256;
        public string execution = "Every source frame manually sampled through the production locomotion player on actual imported Humanoid transforms in an unsaved preview scene.";
        public string scope = "Retargeted bone-landmark measurements. Native source joints/contacts and requested targets remain separate fields; this does not measure rendered skin soles.";
        public string limitations = "Edit-mode geometry only; no live VRM/Animator updates, collision/navigation, latency, planted-foot correction, or subjective naturalness judgment. Native contacts classify stance but cannot establish actual target-avatar contact.";
        public List<NativeAvatarResult> avatars = new List<NativeAvatarResult>();
    }
    [Serializable] private sealed class NativeAvatarResult
    {
        public string avatar, avatarSha256;
        public string contactSheet;
        public int[] contactSheetFrames;
        public float motionScale, maximumMappedRootErrorMetres, maximumMappedHeadingErrorDegrees;
        public float minimumFootLandmarkAboveInitialSupportMetres = float.MaxValue;
        public float maximumNativeContactFootHorizontalSpeedMetresPerSecond;
        public int nativeContactIntervals;
        public List<NativeSample> samples = new List<NativeSample>();
    }
    [Serializable] private sealed class NativeSample
    {
        public int frame;
        public float seconds, nativeLeftFootContact, nativeRightFootContact, nativeHeadingDegrees, targetHeadingDegrees;
        public Vector3 nativePelvis, requestedRoot, actualAvatarRoot, actualHips, actualLeftFoot, actualRightFoot, actualLeftToes, actualRightToes;
        public Quaternion actualAvatarRotation;
    }
    private sealed class Pose
    {
        public Transform transform;
        public Vector3 position, scale;
        public Quaternion rotation;
    }
    private sealed class Fixture : IDisposable
    {
        public Scene Scene;
        public GameObject Root;
        public Vrm10Instance Vrm;
        public ArdyLocomotionPlayer Player;
        public ArdyMotionPlayer UpperBody;
        public ArdyLocomotionAvatarReference Reference;
        public Animator[] Animators;
        public readonly List<Pose> Baseline = new List<Pose>();
        public Vector3 StartPosition;
        public Quaternion StartRotation;
        public float Scale, Ground, MotionScale;
        public string Asset;

        public Fixture(string asset, float yaw, float scale)
        {
            Asset = asset;
            Scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                if (prefab == null) throw new InvalidOperationException("Missing imported avatar: " + asset);
                var imported = prefab.GetComponentInChildren<Vrm10Instance>(true);
                if (imported == null || imported.Humanoid == null) throw new InvalidOperationException("Missing imported VRM humanoid: " + asset);
                // Build a fresh in-memory reference from this exact imported asset, never an old baked reference or animated scene instance.
                Ground = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes }
                    .Select(imported.Humanoid.GetBoneTransform).Where(t => t != null)
                    .Min(t => imported.transform.InverseTransformPoint(t.position).y);
                Reference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, asset);
                Check(Mathf.Abs(Reference.groundY - Ground) < .00001f, "Imported support calibration differs from actual foot/toe landmarks");
                Root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, Scene);
                Vrm = Root.GetComponentInChildren<Vrm10Instance>(true);
                Check(Vrm != null && Vrm.transform == Root.transform, "Fixture requires the VRM wrapper to be the avatar root: " + asset);
                Root.transform.SetPositionAndRotation(new Vector3(1.7f, .35f, -2.4f), Quaternion.Euler(0, yaw, 0));
                Root.transform.localScale = Vector3.one * scale;
                Scale = Root.transform.lossyScale.y;
                StartPosition = Root.transform.position;
                StartRotation = Root.transform.rotation;
                Vrm.enabled = false;
                Animators = Root.GetComponentsInChildren<Animator>(true);
                foreach (var animator in Animators) animator.enabled = false;
                UpperBody = Vrm.gameObject.AddComponent<ArdyMotionPlayer>();
                Player = Vrm.gameObject.AddComponent<ArdyLocomotionPlayer>();
                Player.Bind(Vrm, Reference);
                MotionScale = (Reference.entries.Single(e => e.bone == HumanBodyBones.Hips).positionInRoot.y - Ground) * Scale;
                Check(MotionScale > .1f, "Avatar pelvis support height must be positive");
                foreach (var t in Root.GetComponentsInChildren<Transform>(true))
                    Baseline.Add(new Pose { transform = t, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            }
            catch { Dispose(); throw; }
        }
        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);
        public Quaternion Rest(HumanBodyBones bone) => Reference.entries.Single(e => e.bone == bone).rotationInRoot;
        public void CheckRestored(string label)
        {
            foreach (var p in Baseline)
            {
                Check(Vector3.Distance(p.transform.localPosition, p.position) < .00001f, label + " changed position: " + p.transform.name);
                Check(Quaternion.Angle(p.transform.localRotation, p.rotation) < .08f, label + " changed rotation: " + p.transform.name);
                Check(Vector3.Distance(p.transform.localScale, p.scale) < .00001f, label + " changed scale: " + p.transform.name);
            }
        }
        public void Dispose()
        {
            if (Player != null) Player.RestorePreview();
            if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene);
        }
    }

    private sealed class ContactSheetCapture : IDisposable
    {
        private const int Tile = 384;
        private readonly Fixture fixture;
        private readonly Camera camera;
        private readonly Texture2D sheet;
        private readonly float height;
        private int index;
        public ContactSheetCapture(Fixture f)
        {
            fixture = f;
            sheet = new Texture2D(Tile * 4, Tile * 2, TextureFormat.RGB24, false);
            height = (f.Reference.Get(HumanBodyBones.Head).positionInRoot.y - f.Ground) * f.Scale + .3f;
            var obj = new GameObject("Locomotion contact-sheet camera");
            SceneManager.MoveGameObjectToScene(obj, f.Scene);
            camera = obj.AddComponent<Camera>();
            camera.scene = f.Scene;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.78f, .83f, .87f);
            camera.orthographic = true;
            camera.orthographicSize = height * .65f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 30f;
            for (int i = 0; i < 2; i++)
            {
                var lightObject = new GameObject("Locomotion contact-sheet light " + i);
                SceneManager.MoveGameObjectToScene(lightObject, f.Scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = i == 0 ? 1.1f : .65f;
                light.shadows = LightShadows.None;
                light.transform.rotation = Quaternion.Euler(i == 0 ? 30f : 340f, i == 0 ? 200f : 25f, 0f);
            }
        }
        public void Capture()
        {
            if (index >= 8) throw new InvalidOperationException("Contact sheet accepts eight frames");
            var target = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32);
            var tile = new Texture2D(Tile, Tile, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            var meshes = new List<Mesh>();
            var objects = new List<GameObject>();
            var enabled = new Dictionary<SkinnedMeshRenderer, bool>();
            try
            {
                // Manual edit-mode sampling does not update GPU skinning; explicitly bake each visible mesh for the sampled bone pose.
                foreach (var renderer in fixture.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                    var mesh = new Mesh();
                    renderer.BakeMesh(mesh);
                    meshes.Add(mesh);
                    var baked = new GameObject("Locomotion sampled mesh");
                    baked.transform.SetParent(renderer.transform, false);
                    baked.AddComponent<MeshFilter>().sharedMesh = mesh;
                    baked.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                    objects.Add(baked);
                    enabled[renderer] = renderer.enabled;
                    renderer.enabled = false;
                }
                Check(meshes.Count > 0, "Contact sheet contains no actual baked avatar mesh");
                Vector3 centre = fixture.Root.transform.position + Vector3.up * (fixture.Ground * fixture.Scale + height * .48f);
                camera.transform.position = centre + fixture.StartRotation * new Vector3(2.2f, .8f, 3.7f);
                camera.transform.LookAt(centre);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                tile.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0);
                tile.Apply(false, false);
                var pixels = tile.GetPixels32();
                var background = pixels[0];
                Check(pixels.Count(p => Math.Abs(p.r - background.r) + Math.Abs(p.g - background.g) + Math.Abs(p.b - background.b) > 30) > pixels.Length / 200,
                    "Contact-sheet frame is visually empty");
                sheet.SetPixels32((index % 4) * Tile, (1 - index / 4) * Tile, Tile, Tile, pixels);
                index++;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(tile);
                Object.DestroyImmediate(target);
                foreach (var pair in enabled) pair.Key.enabled = pair.Value;
                foreach (var obj in objects) Object.DestroyImmediate(obj);
                foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            }
        }
        public void Save(string path)
        {
            Check(index == 8, "The contact sheet did not capture all eight requested frames");
            sheet.Apply(false, false);
            File.WriteAllBytes(path, sheet.EncodeToPNG());
        }
        public void Dispose() => Object.DestroyImmediate(sheet);
    }

    /// <summary>Use only in the isolated validation project, without -quit; no existing scene is replaced.</summary>
    public static void RunBatch()
    {
        RequireIsolated();
        string output = Output("ardy-locomotion-regression");
        Directory.CreateDirectory(output);
        var report = new Report { unityVersion = Application.unityVersion,
            playerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.cs"),
            arrivalTimingSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomArrivalTiming.cs"),
            harnessSha256 = Hash("Assets/Editor/ArdyLocomotionRegression.cs") };
        checks = 0;
        try
        {
            ExerciseArrivalTiming(report);
            foreach (string asset in FindAvatars())
                foreach (float yaw in new[] { 0f, 67f })
                    foreach (float scale in new[] { .8f, 1.2f })
                        using (var fixture = new Fixture(asset, yaw, scale)) report.fixtures.Add(Exercise(fixture));
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks;
        File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[ArdyLocomotionRegression] " + report.status + " " + checks + " checks: " + output);
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }

    private static FixtureResult Exercise(Fixture f)
    {
        var result = new FixtureResult { avatar = f.Asset, avatarSha256 = Hash(f.Asset), avatarScale = f.Scale,
            startYaw = f.StartRotation.eulerAngles.y, motionScale = f.MotionScale, mappedBones = f.Player.BoundBoneCount };
        var clip = SyntheticClip();
        ExpectRejected(() => ArdyLocomotionAvatarReference.FromImportedPrefab(f.Vrm, f.Asset), "Capturing an animated scene instance as an imported rest reference");
        f.UpperBody.enabled = true;
        for (int i = 0; i < f.Animators.Length; i++) f.Animators[i].enabled = i % 2 == 0;
        bool[] animatorEnabled = f.Animators.Select(a => a.enabled).ToArray();
        f.Player.Play(clip);
        Check(Mathf.Abs(f.Player.PlaybackEndSeconds - clip.LastSampleSeconds) < .00001f,
            "Default full-body playback unexpectedly shortened a clip.");
        Check(!f.UpperBody.enabled, "Locomotion did not acquire upper-body ownership");
        Check(f.Animators.All(a => !a.enabled), "Locomotion did not acquire Animator ownership");
        f.Player.SampleAt(0);
        Check(Vector3.Distance(f.Root.transform.position, f.StartPosition) < .00001f, "Explicit source origin did not anchor the initial root");
        Check(Quaternion.Angle(f.Root.transform.rotation, f.StartRotation) < .08f, "Explicit source heading did not anchor initial yaw");
        f.Player.SampleAt(.025f);
        Quaternion halfFrameYaw = f.StartRotation * Quaternion.Euler(0, 2.25f, 0);
        Quaternion halfFrameAlignment = f.StartRotation * Quaternion.Inverse(Quaternion.Euler(0, 30, 0));
        Vector3 halfFrameNeutral = f.Reference.Get(HumanBodyBones.Hips).positionInRoot;
        halfFrameNeutral.y = 0;
        halfFrameNeutral *= f.Scale;
        Vector3 halfFramePosition = f.StartPosition + f.StartRotation * halfFrameNeutral
            + halfFrameAlignment * new Vector3(-.01875f, 0, .03125f) * f.MotionScale - halfFrameYaw * halfFrameNeutral;
        Check(Vector3.Distance(f.Root.transform.position, halfFramePosition) < .0001f, "Root interpolation between source frames is incorrect");
        Check(Quaternion.Angle(f.Root.transform.rotation, halfFrameYaw) < .08f, "Heading interpolation between source frames is incorrect");
        f.Player.SampleAt(1f);
        Quaternion routeAlignment = f.StartRotation * Quaternion.Inverse(Quaternion.Euler(0, 30, 0));
        // The protocol is already X-reflected into Unity coordinates. A negative X displacement must not be reflected a second time.
        Quaternion expectedYaw = f.StartRotation * Quaternion.Euler(0, 90, 0);
        Vector3 neutralHips = f.Reference.Get(HumanBodyBones.Hips).positionInRoot;
        Vector3 neutralHorizontal = new Vector3(neutralHips.x, 0, neutralHips.z) * f.Scale;
        Vector3 expectedPosition = f.StartPosition + f.StartRotation * neutralHorizontal
            + routeAlignment * new Vector3(-.75f, 0, 1.25f) * f.MotionScale - expectedYaw * neutralHorizontal;
        result.rootPositionErrorMetres = Vector3.Distance(f.Root.transform.position, expectedPosition);
        result.rootYawErrorDegrees = Quaternion.Angle(f.Root.transform.rotation, expectedYaw);
        Check(result.rootPositionErrorMetres < .0001f, "Canonical root XZ / source heading / scale mapping is incorrect");
        Check(result.rootYawErrorDegrees < .08f, "Avatar root yaw does not match source heading delta");
        Check(Mathf.Abs(f.Root.transform.position.y - f.StartPosition.y) < .00001f, "Pelvis bob was incorrectly applied to the avatar ground transform");
        float expectedHipsHeight = f.StartPosition.y + f.Ground * f.Scale + 1.1f * f.MotionScale;
        result.hipsHeightErrorMetres = Mathf.Abs(f.Bone(HumanBodyBones.Hips).position.y - expectedHipsHeight);
        Check(result.hipsHeightErrorMetres < .0001f, "Source pelvis Y was not mapped onto actual avatar hips");
        Check(Quaternion.Angle(f.Bone(HumanBodyBones.Hips).rotation, expectedYaw * f.Rest(HumanBodyBones.Hips)) < .08f,
            "Source root yaw was applied twice to the hips");
        Quaternion expectedLeg = expectedYaw * Quaternion.Euler(25f, 0, 0) * f.Rest(HumanBodyBones.LeftUpperLeg);
        result.legRotationErrorDegrees = Quaternion.Angle(f.Bone(HumanBodyBones.LeftUpperLeg).rotation, expectedLeg);
        Check(result.legRotationErrorDegrees < .08f, "Actual upper-leg rotation did not receive the independent source leg delta");
        Check(Quaternion.Angle(f.Bone(HumanBodyBones.RightUpperLeg).rotation, expectedYaw * f.Rest(HumanBodyBones.RightUpperLeg)) < .08f,
            "The stationary right leg gained an unintended rotation");
        Check(Quaternion.Angle(f.Bone(HumanBodyBones.Head).rotation, expectedYaw * f.Rest(HumanBodyBones.Head)) < .08f,
            "The full-body head heading was applied twice");
        Check(Quaternion.Angle(f.Bone(HumanBodyBones.LeftFoot).rotation, expectedYaw * Quaternion.Euler(25f, 0, 0) * f.Rest(HumanBodyBones.LeftFoot)) < .08f,
            "The full-body foot did not retain its source global orientation");
        foreach (var p in f.Baseline)
        {
            Check(Vector3.Distance(p.transform.localScale, p.scale) < .00001f, "Retargeting modified bone scale");
            if (p.transform != f.Root.transform && p.transform != f.Bone(HumanBodyBones.Hips))
                Check(Vector3.Distance(p.transform.localPosition, p.position) < .00001f, "Retargeting changed a bone length: " + p.transform.name);
        }
        var beforeRejectedPosition = f.Root.transform.position;
        var beforeRejectedRotation = f.Root.transform.rotation;
        ExpectRejected(() => f.Player.Play(InvalidClip("schema")), "Invalid schema");
        ExpectRejected(() => f.Player.Play(InvalidClip("coordinates")), "Native unreflected coordinates");
        ExpectRejected(() => f.Player.Play(InvalidClip("height")), "Zero source pelvis height");
        ExpectRejected(() => f.Player.Play(InvalidClip("frames")), "Unpaired root and rotation frame counts");
        ExpectRejected(() => f.Player.Play(InvalidClip("quaternion")), "Non-unit source quaternion");
        ExpectRejected(() => f.Player.Play(InvalidClip("nonfinite")), "Non-finite source root");
        ExpectRejected(() => f.Player.Play(InvalidClip("contacts")), "Out-of-range source contact probability");
        ExpectRejected(() => f.Player.Play(InvalidClip("targets")), "Unpaired target count");
        ExpectRejected(() => f.Player.SampleAt(float.NaN), "Non-finite sample time");
        ExpectRejected(() => f.Player.Play(clip, null, -1), "Negative playback endpoint frame");
        ExpectRejected(() => f.Player.Play(clip, null, clip.frames.Length), "Playback endpoint beyond the raw clip");
        Check(Vector3.Distance(f.Root.transform.position, beforeRejectedPosition) < .00001f &&
            Quaternion.Angle(f.Root.transform.rotation, beforeRejectedRotation) < .08f, "Rejected clip mutated the active root");
        Check(!f.UpperBody.enabled && f.Animators.All(a => !a.enabled), "Rejected clip released active locomotion ownership");
        f.Player.Stop();
        Check(Vector3.Distance(f.Root.transform.position, expectedPosition) < .0001f, "Stop teleported the avatar away from its current location");
        Check(Quaternion.Angle(f.Root.transform.rotation, expectedYaw) < .08f, "Stop reset the current heading");
        Check(f.UpperBody.enabled, "Stop did not restore the prior upper-body enabled state");
        for (int i = 0; i < f.Animators.Length; i++) Check(f.Animators[i].enabled == animatorEnabled[i], "Stop did not restore Animator enabled state");
        Check(!f.Vrm.enabled, "Stop enabled an originally disabled VRM component");
        f.Player.RestorePreview();
        f.CheckRestored("RestorePreview after Stop");

        // Playback range is separate from the immutable source. The manually sampled pose
        // must stop at that range as well; subsequent default playback still uses all frames.
        string intactSource = JsonUtility.ToJson(clip);
        f.Player.Play(clip, null, 20);
        f.Player.SampleAt(clip.LastSampleSeconds);
        Check(Mathf.Abs(f.Player.TimeSeconds - 1f) < .00001f && Mathf.Abs(f.Player.PlaybackEndSeconds - 1f) < .00001f,
            "Explicit playback endpoint did not clamp sampling to its selected frame.");
        Check(Vector3.Distance(f.Root.transform.position, expectedPosition) < .0001f &&
            Quaternion.Angle(f.Root.transform.rotation, expectedYaw) < .08f, "Explicit endpoint changed source coordinates or pose sampling.");
        Check(f.Player.Clip == clip && JsonUtility.ToJson(clip) == intactSource,
            "Playback endpoint modified the raw clip instead of choosing a playback range.");
        f.Player.RestorePreview();
        f.CheckRestored("Explicit playback range restoration");
        f.Player.Play(clip);
        Check(Mathf.Abs(f.Player.PlaybackEndSeconds - clip.LastSampleSeconds) < .00001f,
            "A previous explicit playback endpoint leaked into the next default clip.");
        f.Player.RestorePreview();

        // A source trajectory that begins off its requested origin must expose that error in Unity.
        var biased = SyntheticClip();
        biased.frames[0].rootPosition += new Vector3(.12f, 0, -.08f);
        f.UpperBody.enabled = false;
        foreach (var animator in f.Animators) animator.enabled = false;
        f.Player.Play(biased);
        f.Player.SampleAt(0);
        Vector3 expectedBiased = f.StartPosition + routeAlignment * new Vector3(.12f, 0, -.08f) * f.MotionScale;
        Check(Vector3.Distance(f.Root.transform.position, expectedBiased) < .0001f, "First-frame root error was hidden by implicit reanchoring");
        f.Player.SampleAt(1f);
        f.Player.RestorePreview();
        Check(!f.UpperBody.enabled && f.Animators.All(a => !a.enabled), "Preview restored disabled owners as enabled");
        f.CheckRestored("Preview restoration");
        f.Player.Play(clip);
        f.Player.SampleAt(1f);
        f.Player.enabled = false;
        // Non-ExecuteAlways behaviours do not receive the normal lifecycle in this edit-mode fixture.
        // Exercise the preview window's explicit cleanup contract here; actual OnDisable dispatch belongs in PlayMode regression.
        f.Player.RestorePreview();
        Check(!f.Player.HasPoseOwnership && !f.Player.IsPlaying, "Explicit preview cleanup retained locomotion ownership on a disabled component");
        Check(!f.UpperBody.enabled && f.Animators.All(a => !a.enabled), "Explicit disabled-component cleanup changed prior owner enabled states");
        f.CheckRestored("Disabled preview restoration");
        f.Player.enabled = true;
        var worldOrigin = SyntheticClip();
        worldOrigin.sourceOrigin = Vector3.zero;
        worldOrigin.sourceHeadingDegrees = 0;
        f.Player.Play(worldOrigin);
        f.Player.SampleAt(0);
        Quaternion defaultHeading = f.StartRotation * Quaternion.Euler(0, 30, 0);
        Vector3 expectedDefaultOrigin = f.StartPosition + f.StartRotation * neutralHorizontal
            + f.StartRotation * new Vector3(-2, 0, -3) * f.MotionScale - defaultHeading * neutralHorizontal;
        Check(Vector3.Distance(f.Root.transform.position, expectedDefaultOrigin) < .0001f,
            "Default zero source origin was silently replaced by the first generated frame");
        Check(Quaternion.Angle(f.Root.transform.rotation, defaultHeading) < .08f,
            "Default source heading concealed the first generated heading error");
        f.Player.RestorePreview();
        f.CheckRestored("Default-origin preview restoration");
        f.Root.transform.localScale = new Vector3(-1, 1, 1);
        ExpectRejected(() => f.Player.Play(clip), "Reflected avatar world scale");
        f.Root.transform.localScale = Vector3.one * f.Scale;
        Check(!f.Player.HasPoseOwnership, "Rejected avatar scale acquired pose ownership");
        return result;
    }

    private static void ExerciseArrivalTiming(Report report)
    {
        // These deliberately simple piecewise trajectories provide an independent numeric
        // oracle. They test endpoint selection only, never native walking naturalness.
        const int count = 120;
        Vector3 Straight(int i) => new Vector3(Mathf.Min(i / 20f, 2), 0, 0);
        float Zero(int i) => 0;
        var normal = ArrivalTimingClip(Straight, Zero, Straight, Zero);
        // The last fast step ends at frame 40. Three subsequent stable frame intervals
        // satisfy the existing motion check, without a separate post-arrival timer.
        Evaluate("long stationary block padding", normal, 40, 43, 43);

        Vector3 NaturalDeceleration(int i) => new Vector3(i < 30 ? i * 1.96f / 30 :
            i < 40 ? 2 - .0004f * (40 - i) * (40 - i) : 2, 0, 0);
        Evaluate("naturally decelerated arrival has zero added delay",
            ArrivalTimingClip(NaturalDeceleration, Zero, NaturalDeceleration, Zero), 40, 40, 40);

        // Frame 40 is close to the destination but remains part of the final deceleration.
        // Only the repeated exact endpoint from frame 41 starts the requested terminal hold.
        Vector3 SubmillimetreFinalStep(int i) => i == 40 ? new Vector3(1.99994f, 0, 0) : Straight(i);
        Evaluate("submillimetre final deceleration is not terminal hold",
            ArrivalTimingClip(SubmillimetreFinalStep, Zero, SubmillimetreFinalStep, Zero), 41, 43, 43);

        Vector3 VisitThenLeave(int i) => new Vector3(i <= 40 ? i / 20f : i <= 60 ? 2 + (i - 40) / 20f :
            i <= 90 ? 3 - (i - 60) / 30f : 2, 0, 0);
        Evaluate("early visit to final target then leave and return", ArrivalTimingClip(VisitThenLeave, Zero, VisitThenLeave, Zero), 90, 93, 93);

        Vector3 MidPause(int i) => new Vector3(i < 20 ? i / 20f : i < 60 ? 1 : Mathf.Min(2, 1 + (i - 60) / 20f), 0, 0);
        Evaluate("middle pause before continued travel", ArrivalTimingClip(MidPause, Zero, MidPause, Zero), 80, 83, 83);

        Vector3 LateExcursion(int i) => i < 80 ? Straight(i) : new Vector3(i < 90 ? 2 + (i - 80) * .03f :
            i < 100 ? 2.3f - (i - 90) * .03f : 2, 0, 0);
        Evaluate("generated motion leaves target after an apparent stop", ArrivalTimingClip(Straight, Zero, LateExcursion, Zero), 40, 103, 103);

        float FinalTurn(int i) => i < 60 ? 0 : Mathf.Min(90, (i - 60) * 2.25f);
        Evaluate("planned final turn must complete", ArrivalTimingClip(Straight, FinalTurn, Straight, FinalTurn), 100, 103, 103);

        float LateYawExcursion(int i) => i < 60 ? 0 : i < 80 ? (i - 60) * 4.5f : i < 100 ? 90 - (i - 80) * 4.5f : 0;
        Evaluate("generated late heading excursion cannot be cut away", ArrivalTimingClip(Straight, Zero, Straight, LateYawExcursion), 40, 103, 103);

        Vector3 ShortHold(int i) => new Vector3(Mathf.Min(2, i * 2f / 117), 0, 0);
        Evaluate("too little removable tail retains full clip", ArrivalTimingClip(ShortHold, Zero, ShortHold, Zero), 117, 119, 119);

        Vector3 NeverHold(int i) => new Vector3(i * 2f / (count - 1), 0, 0);
        Evaluate("no terminal requested hold retains full clip", ArrivalTimingClip(NeverHold, Zero, NeverHold, Zero), 119, 119, 119);

        Vector3 MissedTarget(int i) => Straight(i) + (i >= 40 ? new Vector3(.09f, 0, 0) : Vector3.zero);
        Evaluate("broad arrival tolerance cannot hide nine centimetre miss", ArrivalTimingClip(Straight, Zero, MissedTarget, Zero), 40, 119, 119);

        Vector3 ShakingRoot(int i) => Straight(i) + (i >= 40 ? new Vector3(i % 2 == 0 ? .02f : -.02f, 0, 0) : Vector3.zero);
        Evaluate("root velocity remains high despite small displacement", ArrivalTimingClip(Straight, Zero, ShakingRoot, Zero), 40, 119, 119);

        float WrappedTarget(int i) => i < 40 ? i * 4.5f : i % 2 == 0 ? 180 : -180;
        float WrappedActual(int i) => Mathf.Min(180, i * 4.5f);
        Evaluate("equivalent headings across plus-minus 180 degrees", ArrivalTimingClip(Straight, WrappedTarget, Straight, WrappedActual), 40, 43, 43);

        float WrongHeading(int i) => Mathf.Min(90, i * 2.25f);
        Evaluate("stable but wrong final heading retains full clip", ArrivalTimingClip(Straight, Zero, Straight, WrongHeading), 40, 119, 119);

        Vector3 TightMiss(int i) => Straight(i) + (i >= 40 ? new Vector3(.015f, 0, 0) : Vector3.zero);
        Evaluate("caller smaller arrival tolerance remains binding", ArrivalTimingClip(Straight, Zero, TightMiss, Zero), 40, 119, 119, .01f);

        void Evaluate(string name, ArdyLocomotionClip candidate, int terminal, int earliest, int latest, float tolerance = .2f)
        {
            string before = JsonUtility.ToJson(candidate);
            var worldRoots = candidate.frames.Select(f => new Vector3(f.rootPosition.x, 0, f.rootPosition.z)).ToArray();
            var selected = ArdyRoomArrivalTiming.Select(candidate, worldRoots, new Vector3(2, 0, 0), tolerance);
            Check(selected.TerminalHoldStartFrame == terminal, name + ": selected the wrong final constant target segment.");
            Check(selected.PlaybackEndFrame >= earliest && selected.PlaybackEndFrame <= latest,
                name + ": expected endpoint frame in [" + earliest + ", " + latest + "], received " + selected.PlaybackEndFrame + ".");
            Check(Mathf.Abs(selected.PlaybackEndSeconds - selected.PlaybackEndFrame / 20f) < .0001f &&
                Mathf.Abs(selected.PlannedArrivalSeconds - terminal / 20f) < .0001f &&
                Mathf.Abs(selected.SkippedTailSeconds - (119 - selected.PlaybackEndFrame) / 20f) < .0001f,
                name + ": seconds do not match the independent known 20 fps timeline.");
            Check(candidate.frames.Length == count && candidate.rotationClip.frames.Length == count && candidate.targets.Length == count &&
                JsonUtility.ToJson(candidate) == before, name + ": endpoint selection mutated raw generated output.");
            report.arrivalTimingFixtures.Add(new ArrivalTimingResult {
                name = name, rawFrames = candidate.frames.Length, terminalHoldStartFrame = selected.TerminalHoldStartFrame,
                selectedEndFrame = selected.PlaybackEndFrame, rawLastSampleSeconds = candidate.LastSampleSeconds,
                selectedEndSeconds = selected.PlaybackEndSeconds, plannedArrivalSeconds = selected.PlannedArrivalSeconds,
                skippedTailSeconds = selected.SkippedTailSeconds
            });
        }
    }

    private static ArdyLocomotionClip ArrivalTimingClip(Func<int, Vector3> targetPosition, Func<int, float> targetHeading,
        Func<int, Vector3> generatedPosition, Func<int, float> generatedHeading)
    {
        var value = SyntheticClip();
        value.id = value.rotationClip.id = "synthetic-arrival-timing-only";
        value.rotationClip.text = "Piecewise numeric endpoint selection fixture; no ARDY inference took place.";
        value.sourceOrigin = Vector3.zero; value.sourceHeadingDegrees = 0;
        value.frames = new ArdyLocomotionFrame[120]; value.targets = new ArdyLocomotionTarget[120];
        value.rotationClip.frames = new ArdyMotionFrame[120];
        for (int i = 0; i < value.frames.Length; i++)
        {
            value.frames[i] = new ArdyLocomotionFrame { rootPosition = generatedPosition(i) + Vector3.up,
                leftFootContact = 1, rightFootContact = 1 };
            value.targets[i] = new ArdyLocomotionTarget { rootPosition = targetPosition(i), headingDegrees = targetHeading(i) };
            value.rotationClip.frames[i] = new ArdyMotionFrame { globalRotations =
                Enumerable.Repeat(Quaternion.Euler(0, generatedHeading(i), 0), 27).ToArray() };
        }
        value.Validate();
        return value;
    }

    private static ArdyLocomotionClip SyntheticClip()
    {
        const int count = 41;
        var rotations = new ArdyMotionFrame[count];
        var frames = new JArray();
        var targets = new JArray();
        for (int i = 0; i < count; i++)
        {
            float amount = Mathf.Min(1f, i / 20f);
            var heading = Quaternion.Euler(0, 30 + 90 * amount, 0);
            var q = Enumerable.Repeat(heading, 27).ToArray();
            for (int j = 23; j < 27; j++) q[j] = heading * Quaternion.Euler(25f * amount, 0, 0);
            rotations[i] = new ArdyMotionFrame { globalRotations = q };
            frames.Add(new JObject { ["rootPosition"] = VectorJson(new Vector3(-2 - .75f * amount, 1 + .1f * amount, -3 + 1.25f * amount)),
                ["leftFootContact"] = 0f, ["rightFootContact"] = 1f });
            targets.Add(new JObject { ["rootPosition"] = VectorJson(new Vector3(-2 - .75f * amount, 0, -3 + 1.25f * amount)),
                ["headingDegrees"] = 30 + 90 * amount });
        }
        var rotationClip = new ArdyMotionClip { schema = 1, id = "synthetic-locomotion-coordinate-check", text = "Synthetic coordinate fixture; not native generation.",
            fps = 20, jointNames = SourceNames, jointParents = SourceParents, restGlobalRotations = Enumerable.Repeat(Quaternion.identity, 27).ToArray(),
            frames = rotations, source = new ArdyMotionSource { coordinateSystem = "unity-lh-y-up-z-forward", rootTranslationApplied = false } };
        var root = new JObject { ["schema"] = 1, ["id"] = rotationClip.id, ["sourceRootHeight"] = 1f,
            ["sourceOrigin"] = VectorJson(new Vector3(-2, 0, -3)), ["sourceHeadingDegrees"] = 30f,
            ["rotationClip"] = JObject.Parse(JsonUtility.ToJson(rotationClip)), ["frames"] = frames, ["targets"] = targets,
            ["source"] = new JObject { ["mode"] = "ardy-locomotion-native", ["generation"] = "synthetic-regression-only", ["coordinateSystem"] = "unity-lh-y-up-z-forward",
                ["testProvenance"] = "SYNTHETIC regression trajectory: protocol mode is exercised, no ARDY inference took place." } };
        return ArdyLocomotionClip.Parse(root.ToString());
    }

    private static ArdyLocomotionClip InvalidClip(string kind)
    {
        var clip = SyntheticClip();
        switch (kind)
        {
            case "schema": clip.schema = 99; break;
            case "coordinates": clip.source.coordinateSystem = "ardy-rh-y-up-z-forward"; break;
            case "height": clip.sourceRootHeight = 0; break;
            case "frames": clip.frames = clip.frames.Take(clip.frames.Length - 1).ToArray(); break;
            case "quaternion": clip.rotationClip.frames[0].globalRotations[0] = new Quaternion(0, 0, 0, 0); break;
            case "nonfinite": clip.frames[0].rootPosition = new Vector3(float.NaN, 1, 0); break;
            case "contacts": clip.frames[0].leftFootContact = 1.5f; break;
            case "targets": clip.targets = clip.targets.Take(clip.targets.Length - 1).ToArray(); break;
            default: throw new ArgumentException(kind);
        }
        return clip;
    }

    /// <summary>Pass -ardyLocomotionClip absolute.json, optional -ardyLocomotionAvatar asset.vrm and -ardyLocomotionRender (requires graphics).</summary>
    public static void AnalyzeBatch()
    {
        RequireIsolated();
        string output = Output("ardy-locomotion-native-retarget");
        Directory.CreateDirectory(output);
        var report = new NativeReport { unityVersion = Application.unityVersion, inputPath = Argument("-ardyLocomotionClip"),
            playerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.cs"),
            harnessSha256 = Hash("Assets/Editor/ArdyLocomotionRegression.cs") };
        try
        {
            if (string.IsNullOrEmpty(report.inputPath)) throw new ArgumentException("-ardyLocomotionClip is required");
            report.inputPath = Path.GetFullPath(report.inputPath);
            report.inputSha256 = Hash(report.inputPath);
            var clip = ArdyLocomotionClip.Parse(File.ReadAllText(report.inputPath));
            foreach (string asset in FindAvatars())
                using (var f = new Fixture(asset, 37f, 1f)) report.avatars.Add(MeasureNative(f, clip, output));
            report.status = "measured";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        File.WriteAllText(Path.Combine(output, "retarget-report.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[ArdyLocomotionRegression] Native retarget " + report.status + ": " + output);
        EditorApplication.Exit(report.status == "measured" ? 0 : 1);
    }

    private static NativeAvatarResult MeasureNative(Fixture f, ArdyLocomotionClip clip, string output)
    {
        float motionScale = f.MotionScale / clip.sourceRootHeight;
        float support = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes }
            .Select(f.Bone).Where(t => t != null).Min(t => t.position.y);
        var result = new NativeAvatarResult { avatar = f.Asset, avatarSha256 = Hash(f.Asset), motionScale = motionScale };
        Quaternion alignment = f.StartRotation * Quaternion.Inverse(Quaternion.Euler(0, clip.sourceHeadingDegrees, 0));
        bool render = Array.IndexOf(Environment.GetCommandLineArgs(), "-ardyLocomotionRender") >= 0;
        if (render && clip.frames.Length < 8) throw new ArgumentException("Contact-sheet rendering requires at least eight source frames");
        var selected = Enumerable.Range(0, 8).Select(i => Mathf.RoundToInt(i * (clip.frames.Length - 1) / 7f)).ToArray();
        using (var capture = render ? new ContactSheetCapture(f) : null)
        {
        f.Player.Play(clip);
        for (int i = 0; i < clip.frames.Length; i++)
        {
            float seconds = i / clip.rotationClip.fps;
            f.Player.SampleAt(seconds);
            var frame = clip.frames[i];
            var sourceHeadingVector = clip.rotationClip.SampleDelta(0, seconds) * Vector3.forward;
            float heading = Mathf.Atan2(sourceHeadingVector.x, sourceHeadingVector.z) * Mathf.Rad2Deg;
            var sample = new NativeSample { frame = i, seconds = seconds, nativePelvis = frame.rootPosition,
                nativeLeftFootContact = frame.leftFootContact, nativeRightFootContact = frame.rightFootContact,
                nativeHeadingDegrees = heading, requestedRoot = clip.targets[i].rootPosition, targetHeadingDegrees = clip.targets[i].headingDegrees,
                actualAvatarRoot = f.Root.transform.position, actualAvatarRotation = f.Root.transform.rotation,
                actualHips = f.Bone(HumanBodyBones.Hips).position, actualLeftFoot = f.Bone(HumanBodyBones.LeftFoot).position,
                actualRightFoot = f.Bone(HumanBodyBones.RightFoot).position,
                actualLeftToes = (f.Bone(HumanBodyBones.LeftToes) ?? f.Bone(HumanBodyBones.LeftFoot)).position,
                actualRightToes = (f.Bone(HumanBodyBones.RightToes) ?? f.Bone(HumanBodyBones.RightFoot)).position };
            var displacement = frame.rootPosition - clip.sourceOrigin;
            displacement.y = 0;
            Vector3 neutralHips = f.Reference.Get(HumanBodyBones.Hips).positionInRoot;
            Vector3 neutralHorizontal = new Vector3(neutralHips.x, 0, neutralHips.z) * f.Scale;
            Vector3 expectedRoot = f.StartPosition + f.StartRotation * neutralHorizontal + alignment * displacement * motionScale
                - (alignment * Quaternion.Euler(0, heading, 0)) * neutralHorizontal;
            result.maximumMappedRootErrorMetres = Mathf.Max(result.maximumMappedRootErrorMetres, Vector3.Distance(expectedRoot, sample.actualAvatarRoot));
            result.maximumMappedHeadingErrorDegrees = Mathf.Max(result.maximumMappedHeadingErrorDegrees,
                Quaternion.Angle(sample.actualAvatarRotation, alignment * Quaternion.Euler(0, heading, 0)));
            result.minimumFootLandmarkAboveInitialSupportMetres = Mathf.Min(result.minimumFootLandmarkAboveInitialSupportMetres,
                Mathf.Min(Mathf.Min(sample.actualLeftFoot.y, sample.actualRightFoot.y), Mathf.Min(sample.actualLeftToes.y, sample.actualRightToes.y)) - support);
            if (i > 0)
            {
                var previous = result.samples[i - 1];
                if (sample.nativeLeftFootContact >= .5f && previous.nativeLeftFootContact >= .5f)
                    AddSpeed(previous.actualLeftFoot, sample.actualLeftFoot);
                if (sample.nativeRightFootContact >= .5f && previous.nativeRightFootContact >= .5f)
                    AddSpeed(previous.actualRightFoot, sample.actualRightFoot);
            }
            result.samples.Add(sample);
            if (render && Array.IndexOf(selected, i) >= 0) capture.Capture();
        }
        if (render)
        {
            result.contactSheet = Path.Combine(output, Path.GetFileNameWithoutExtension(f.Asset) + "-contact-sheet.png");
            result.contactSheetFrames = selected;
            capture.Save(result.contactSheet);
        }
        }
        f.Player.RestorePreview();
        f.CheckRestored("Native preview restoration");
        return result;
        void AddSpeed(Vector3 before, Vector3 after)
        {
            Vector3 step = after - before; step.y = 0;
            result.nativeContactIntervals++;
            result.maximumNativeContactFootHorizontalSpeedMetresPerSecond = Mathf.Max(result.maximumNativeContactFootHorizontalSpeedMetresPerSecond, step.magnitude * clip.rotationClip.fps);
        }
    }

    private static string[] FindAvatars()
    {
        string requested = Argument("-ardyLocomotionAvatar");
        var paths = string.IsNullOrEmpty(requested)
            ? AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/Model/", StringComparison.Ordinal) && p.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
            : new[] { requested }.AsEnumerable();
        var usable = paths.OrderBy(p => p, StringComparer.Ordinal).Where(p => {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            var vrm = prefab != null ? prefab.GetComponentInChildren<Vrm10Instance>(true) : null;
            return vrm != null && vrm.Humanoid != null && vrm.Humanoid.GetBoneTransform(HumanBodyBones.Hips) != null
                && vrm.Humanoid.GetBoneTransform(HumanBodyBones.LeftFoot) != null && vrm.Humanoid.GetBoneTransform(HumanBodyBones.RightFoot) != null;
        }).ToArray();
        if (usable.Length == 0) throw new InvalidOperationException("No imported VRM 1.0 avatar with hips and both feet found in Assets/Model.");
        return usable;
    }
    private static JObject VectorJson(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, label); return; }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new InvalidOperationException(label + " was accepted");
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    private static string Output(string fallback) => Path.GetFullPath(Argument("-ardyLocomotionOutput") ?? Path.Combine(Application.dataPath, "../Logs/" + fallback));
    private static string Hash(string path)
    {
        using (var hash = SHA256.Create())
        using (var input = File.OpenRead(Path.GetFullPath(path)))
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
    private static void RequireIsolated()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run this entry point only in the isolated unity-naturalness-validation project with -batchmode.");
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Locomotion geometry sampling requires edit mode.");
    }
}
