using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Perception;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Actual authored SailingMoon lamp cloned into an isolated Play fixture; no scene save, model or HTTP.</summary>
[InitializeOnLoad]
public static class SceneObjectLightRegression
{
    private const string Key = "NeEEvA.SceneObjectLightRegression.";
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string FixtureName = "SceneObjectLightRegression-explicit-room";
    [Serializable] private sealed class Report
    {
        public string status = "running", error, graphics, entityId;
        public int checks;
        public float brightestOn, correspondingOff;
        public bool sourceSceneUnchanged, materialAssetUnchanged;
        public List<string> cases = new List<string>();
        public string scope = "Actual authored lamp geometry/materials/Animator/Light in an isolated runtime clone. Tests whitelist registration, real component/emission mutation, GPU appearance, restore and external-change ownership. No physical hand interaction, character-view visibility, navigation, model understanding or removal of baked room illumination assertion.";
    }
    private static Report report;
    private static bool running;

    static SceneObjectLightRegression()
    {
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Exercise;
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.update += () => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue) &&
                !SessionState.GetBool(Key + "Finished", false)) Finish(new TimeoutException("Lamp regression deadline."));
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only use the existing isolated Unity validation project.");
        string[] args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-sceneObjectLightOutput");
        string output = Path.GetFullPath(at >= 0 && at + 1 < args.Length ? args[at + 1] : "Logs/scene-object-light");
        Directory.CreateDirectory(output); SessionState.SetString(Key + "Output", output);
        SessionState.SetString(Key + "SceneBefore", FileHash(ScenePath));
        Scene source = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        try
        {
            Transform lamp = source.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Single(item => item.name == "standlight (4)");
            var material = lamp.Find("スタンドライト.001").GetComponent<Renderer>().sharedMaterial;
            string materialPath = AssetDatabase.GetAssetPath(material);
            SessionState.SetString(Key + "MaterialPath", materialPath);
            SessionState.SetString(Key + "MaterialBefore", FileHash(materialPath));
            Scene fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(fixture);
            var environment = new GameObject(FixtureName);
            var furniture = new GameObject("1階家具"); furniture.transform.SetParent(environment.transform, false);
            var clone = Object.Instantiate(lamp.gameObject, furniture.transform, false);
            clone.name = "standlight (4)"; clone.transform.localPosition = Vector3.zero;
            // All geometry/materials/animation references remain the authored ones. The fixture only relocates the whole lamp.
        }
        finally { EditorSceneManager.CloseScene(source, true); }
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 75f);
        EditorApplication.isPlaying = true;
    }

    private static void Exercise()
    {
        if (running || !SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        running = true; report = new Report { graphics = SystemInfo.graphicsDeviceType.ToString() };
        SceneObjectLightSwitch device = null;
        try
        {
            var environment = GameObject.Find(FixtureName).transform;
            var root = environment.Find("1階家具/standlight (4)");
            var glow = root.Find("スタンドライト.001").GetComponent<Renderer>();
            var light = root.Find("Point Light").GetComponent<Light>();
            Material original = glow.sharedMaterial;
            Color originalEmission = original.GetColor("_EmissionColor");
            bool originalEnabled = light.enabled;
            float originalIntensity = light.intensity;
            Check(!SceneObjectLampCatalog.EnsureRegistered(null, out _, out _), "No global or missing-room lamp discovery.");
            Check(SceneObjectLampCatalog.EnsureRegistered(environment, out device, out string error), "Exact authored profile registers: " + error);
            report.entityId = device.EntityId;
            Check(device.Identity != null && device.Identity.Root == root && device.EntityId.Length > 0 &&
                device.Identity.SceneLabel == SceneObjectLampCatalog.Label, "One authored identity owns the lamp capability.");
            Check(glow.sharedMaterial != original && original.GetColor("_EmissionColor") == originalEmission,
                "Binding creates an owned material instance without mutating the shared asset.");
            Check(SceneObjectLampCatalog.EnsureRegistered(environment, out var same, out _) && same == device &&
                root.GetComponents<SceneObjectLightSwitch>().Length == 1, "Registration is idempotent.");
            Check(device.ReadState().bakedLightingResidualUncontrolled && light.lightmapBakeType == LightmapBakeType.Baked,
                "Authored baked Light remains baked and its uncontrolled illumination is disclosed.");
            Check(device.TrySetPower("off-first", false, out var off, out error) && off.completed && off.changed &&
                !off.actual.emissionEnabled && !off.actual.lightEnabled && !off.physicalSwitchPress && !off.freshVisualObservation,
                "Off changes real emission and Light components without claiming a physical press or new view: " + error);
            Check(!glow.sharedMaterial.IsKeywordEnabled("_EMISSION") && glow.sharedMaterial.GetColor("_EmissionColor") == Color.black && !light.enabled,
                "Actual rendered emission parameters and Light.enabled are off.");
            Check(device.TrySetPower("off-first", false, out var duplicate, out _) && duplicate.replayed && !duplicate.changed,
                "Same action replay is idempotent.");
            Check(!device.TrySetPower("off-first", true, out _, out _), "An action ID cannot change its intended power.");
            Check(device.TrySetPower("on-second", true, out var on, out _) && on.completed && on.actual.emissionEnabled && light.enabled,
                "On restores real emissive appearance and enables the Light.");
            Check(!device.TrySetPower("off-first", false, out _, out _) && device.ReadState().emissionEnabled,
                "Late retry of an old Off cannot override a later On.");
            Check(original.GetColor("_EmissionColor") == originalEmission && original.IsKeywordEnabled("_EMISSION"),
                "On/off never changes original material properties.");
            report.cases.Add("exact-authored-registration-real-on-off-baked-disclosure-idempotence-and-stale-action-fence");

            RenderAppearance(device, root);

            Check(device.RestoreOriginal(out error) && glow.sharedMaterial == original && light.enabled == originalEnabled,
                "Explicit restore returns owned slots and Light state to their original values: " + error);
            Check(SceneObjectLampCatalog.EnsureRegistered(environment, out device, out error), "Rebind after restoration: " + error);
            Check(device.TrySetPower("external-reference-off", false, out _, out _), "Prepare external material-reference conflict.");
            var foreign = new Material(original) { name = "User-owned replacement material" };
            glow.sharedMaterial = foreign;
            Check(!device.TrySetPower("external-reference-on", true, out var rejected, out _) && !rejected.completed,
                "External material replacement prevents subsequent lamp writes.");
            Check(!device.RestoreOriginal(out _) && glow.sharedMaterial == foreign,
                "Restore preserves an externally replaced material.");
            glow.sharedMaterial = original; Object.Destroy(foreign);

            Check(SceneObjectLampCatalog.EnsureRegistered(environment, out device, out error), "Rebind owned material-edit case: " + error);
            Check(device.TrySetPower("edited-owned-off", false, out _, out _), "Prepare owned instance external edit.");
            Material editedInstance = glow.sharedMaterial;
            editedInstance.SetColor("_Color", Color.red);
            Check(!device.TryValidate(out _) && !device.TrySetPower("edited-owned-on", true, out _, out _),
                "Even a non-emission material edit revokes material ownership.");
            Check(!device.RestoreOriginal(out _) && glow.sharedMaterial == editedInstance && editedInstance.GetColor("_Color") == Color.red,
                "Restore does not discard the user's edit to the owned material instance.");
            glow.sharedMaterial = original; Object.Destroy(editedInstance);

            Check(SceneObjectLampCatalog.EnsureRegistered(environment, out device, out error), "Rebind external Light case: " + error);
            Check(device.TrySetPower("light-conflict-off", false, out _, out _), "Prepare external Light edit.");
            light.intensity = originalIntensity + 1;
            Check(!device.TrySetPower("light-conflict-on", true, out _, out _), "Changed Light configuration prevents a silent overwrite.");
            device.RestoreOriginal(out _);
            Check(Mathf.Approximately(light.intensity, originalIntensity + 1) && !light.enabled,
                "Restore leaves all externally edited Light state intact, including its current power.");
            light.intensity = originalIntensity; light.enabled = originalEnabled;
            Check(SceneObjectLampCatalog.EnsureRegistered(environment, out device, out error), "Rebind lifetime case: " + error);
            Check(device.TrySetPower("lifetime-off", false, out _, out _), "Prepare capability disable.");
            device.enabled = false;
            Check(glow.sharedMaterial == original && light.enabled == originalEnabled && !device.IsBound,
                "Disabling the capability restores its own untouched original state.");
            Check(!SceneObjectLampCatalog.EnsureRegistered(environment, out _, out _), "Registration never silently reenables a disabled capability.");
            report.cases.Add("material-reference-edit-owned-property-edit-light-configuration-and-disable-restore");

            var duplicateRoot = Object.Instantiate(root.gameObject, root.parent, false); duplicateRoot.name = "standlight (4)";
            Check(!SceneObjectLampCatalog.EnsureRegistered(environment, out _, out _), "Ambiguous exact names are rejected rather than choosing a lamp.");
            Object.DestroyImmediate(duplicateRoot);
            root.name = "standlight (40)";
            Check(!SceneObjectLampCatalog.EnsureRegistered(environment, out _, out _), "A similar name is not an authorized lamp profile.");
            root.name = "standlight (4)";
            report.cases.Add("no-ambiguous-or-fuzzy-registration");
            report.sourceSceneUnchanged = FileHash(ScenePath) == SessionState.GetString(Key + "SceneBefore", "");
            report.materialAssetUnchanged = FileHash(SessionState.GetString(Key + "MaterialPath", "")) == SessionState.GetString(Key + "MaterialBefore", "");
            Check(report.sourceSceneUnchanged && report.materialAssetUnchanged, "Authored scene and material files remain byte-identical.");
            Finish(null);
        }
        catch (Exception error) { device?.RestoreOriginal(out _); Finish(error); }
    }

    private static void RenderAppearance(SceneObjectLightSwitch device, Transform root)
    {
        Check(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A real graphics device is required for emission appearance.");
        var cameraObject = new GameObject("Lamp appearance proof camera"); var camera = cameraObject.AddComponent<Camera>();
        var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32); rt.Create();
        camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        camera.targetTexture = rt; camera.fieldOfView = 35; camera.nearClipPlane = .001f; camera.farClipPlane = 10f;
        RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.black; RenderSettings.ambientIntensity = 0; RenderSettings.reflectionIntensity = 0;
        var bounds = root.GetComponent<Renderer>().bounds; bounds.Encapsulate(device.EmissionRenderer.bounds);
        float distance = Mathf.Max(.15f, bounds.size.magnitude * 2.5f);
        string output = SessionState.GetString(Key + "Output", "");
        try
        {
            float bestDifference = -1;
            for (int i = 0; i < 4; i++)
            {
                Vector3 offset = Quaternion.AngleAxis(i * 90f, Vector3.up) * new Vector3(0, .25f, -1).normalized * distance;
                camera.transform.position = bounds.center + offset; camera.transform.LookAt(bounds.center);
                Check(device.TrySetPower("render-on-" + i, true, out _, out _), "Prepare actual On render.");
                float on = Capture(camera, rt, Path.Combine(output, "lamp-on-view-" + i + ".png"));
                Check(device.TrySetPower("render-off-" + i, false, out _, out _), "Prepare actual Off render.");
                float off = Capture(camera, rt, Path.Combine(output, "lamp-off-view-" + i + ".png"));
                if (on - off > bestDifference) { bestDifference = on - off; report.brightestOn = on; report.correspondingOff = off; }
            }
            Check(bestDifference > .0005f, "Actual authored emissive mesh produces a measurable On/Off image change.");
            report.cases.Add("GPU-on-off-four-authored-lamp-viewpoints-no-room-baked-removal-claim");
        }
        finally { camera.targetTexture = null; rt.Release(); Object.Destroy(rt); Object.Destroy(cameraObject); }
    }

    private static float Capture(Camera camera, RenderTexture target, string path)
    {
        camera.Render(); var previous = RenderTexture.active;
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        try
        {
            RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            double total = 0; foreach (var color in texture.GetPixels32()) total += color.r + color.g + color.b;
            return (float)(total / (target.width * target.height * 3d * 255d));
        }
        finally { RenderTexture.active = previous; Object.Destroy(texture); }
    }

    private static void Check(bool valid, string message) { report.checks++; if (!valid) throw new InvalidOperationException(message); }
    private static string FileHash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream));
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        if (report == null) report = new Report(); report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        File.WriteAllText(Path.Combine(SessionState.GetString(Key + "Output", ""), "report.json"), JsonUtility.ToJson(report, true));
        SessionState.SetBool(Key + "Finished", true); SessionState.SetInt(Key + "Exit", error == null ? 0 : 1);
        if (error != null) Debug.LogException(error);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    private static void Exit()
    {
        int code = SessionState.GetInt(Key + "Exit", 1); SessionState.SetBool(Key + "Active", false);
        EditorApplication.Exit(code);
    }
}
