using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Graphics-required capture lifecycle checks in an unsaved isolated scene, using the real VRM.</summary>
[InitializeOnLoad]
public static class NevaSceneVisionCaptureRegression
{
    private const string Key = "NeEEvA.SceneVisionCaptureRegression.";
    private const string Actor = "Scene vision isolated VRM";
    private const string AvatarPath = "Assets/Model/NeEEvA.vrm";
    [Serializable] private sealed class Report
    {
        public string status = "running", error, graphicsDevice, graphicsApi, snapshotMetadata;
        public string scope = "Real imported VRM, temporary unsaved isolated scene, asymmetric visible RGB landmarks. Checks render orientation and capture lifecycle; no Qwen semantic, TV desktop, room navigation or actual-room coverage claim.";
        public bool uvStartsAtTop, asyncReadback, renderedInFlight, closedCleared, rendererRestored, cameraUnchanged;
        public int checks, callbacks, cancelCallbacks, sourceRenderers;
        public float preparationMilliseconds;
        public Color topLeft, topRight, bottomLeft, bottomRight;
    }
    private static Report report;
    private static NevaSceneVisionCapture source;
    private static Camera userCamera;
    private static Renderer[] originalRenderers;
    private static bool[] originalForceOff, originalEnabled;
    private static int[] originalLayers;
    private static SceneVisionObservation result;
    private static int phase;
    private static float phaseAt;
    private static Vector3 userPosition;
    private static Quaternion userRotation;
    private static int userMask;
    private static bool userEnabled;

    static NevaSceneVisionCaptureRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                try { Initialize(); } catch (Exception e) { Finish(e); }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("This regression only runs in the existing isolated validation project.");
        SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-nevaSceneVisionOutput", "Logs/neva-scene-vision-capture")));
        try
        {
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var actor = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath), scene);
            actor.name = Actor;
            foreach (var component in actor.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
            var avatar = actor.GetComponentInChildren<Vrm10Instance>(true);
            var capture = actor.AddComponent<NevaSceneVisionCapture>();
            capture.Configure(avatar); // Authored neutral edit-mode reference, serialized through Play entry.
            if (!capture.TryGetCurrentCameraPose(out Pose pose)) throw new InvalidOperationException("Neutral eye calibration unavailable.");
            MakePanel(pose, new Vector3(-1.2f, .7f, 2), Color.red, "Top left red");
            MakePanel(pose, new Vector3(1.2f, .7f, 2), Color.green, "Top right green");
            MakePanel(pose, new Vector3(-1.2f, -.7f, 2), Color.blue, "Bottom left blue");
            MakePanel(pose, new Vector3(1.2f, -.7f, 2), Color.yellow, "Bottom right yellow");
            var observer = new GameObject("Independent user camera").AddComponent<Camera>();
            observer.transform.SetPositionAndRotation(new Vector3(3, 2, -4), Quaternion.Euler(12, -30, 0));
            observer.fieldOfView = 53; observer.cullingMask = ~0; observer.enabled = false;
            var sentinel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sentinel.name = "Hidden head renderer sentinel";
            sentinel.transform.SetParent(avatar.Humanoid.GetBoneTransform(HumanBodyBones.Head), false);
            sentinel.transform.localScale = Vector3.one * .005f;
            sentinel.GetComponent<Renderer>().forceRenderingOff = true;
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 45);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e) { Finish(e); }
    }

    private static void MakePanel(Pose pose, Vector3 localPosition, Color color, string name)
    {
        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube); panel.name = name;
        panel.transform.SetPositionAndRotation(pose.position + pose.rotation * localPosition, pose.rotation);
        panel.transform.localScale = new Vector3(2.4f, 1.4f, .02f);
        var shader = Shader.Find("Unlit/Color");
        if (shader == null) throw new InvalidOperationException("Unlit/Color unavailable.");
        panel.GetComponent<Renderer>().sharedMaterial = new Material(shader) { color = color };
    }

    private static void Initialize()
    {
        report = new Report { graphicsDevice = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            uvStartsAtTop = SystemInfo.graphicsUVStartsAtTop, asyncReadback = SystemInfo.supportsAsyncGPUReadback };
        Require(SystemInfo.supportsAsyncGPUReadback, "Graphics-enabled run with async readback is required; do not pass -nographics.");
        source = GameObject.Find(Actor).GetComponent<NevaSceneVisionCapture>();
        userCamera = GameObject.Find("Independent user camera").GetComponent<Camera>();
        originalRenderers = source.Target.GetComponentsInChildren<Renderer>(true);
        originalForceOff = originalRenderers.Select(r => r.forceRenderingOff).ToArray();
        originalEnabled = originalRenderers.Select(r => r.enabled).ToArray();
        originalLayers = originalRenderers.Select(r => r.gameObject.layer).ToArray();
        report.sourceRenderers = originalRenderers.Length;
        userPosition = userCamera.transform.position; userRotation = userCamera.transform.rotation;
        userMask = userCamera.cullingMask; userEnabled = userCamera.enabled;
        source.SetObserving(true);
        Require(source.LatestObservation != null && !source.LatestObservation.HasImage && source.PreviewTexture == null, "Opening has no stale image.");
        source.RequestCapture(value => { report.callbacks++; result = value; });
        phase = 0; phaseAt = Time.realtimeSinceStartup;
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0))
        { Finish(new TimeoutException("Capture regression exceeded its bounded deadline.")); return; }
        if (!EditorApplication.isPlaying || source == null) return;
        try
        {
            if (phase == 0 && result != null)
            {
                Require(report.callbacks == 1 && result.HasImage, "One successful fresh capture: " + result.State + " / " + result.Reason);
                Require(result.Frame >= 0 && result.CapturedAtRealtime >= 0 && result.SceneHandle == source.gameObject.scene.handle, "Snapshot freezes render frame/time/scene identity.");
                Require(result.MetadataJson.IndexOf("base64", StringComparison.Ordinal) < 0, "Metadata excludes pixels.");
                Require(source.IsCurrentViewCompatible(result), "Current view accepts this fresh observation.");
                report.snapshotMetadata = result.MetadataJson; report.preparationMilliseconds = source.PreparationMilliseconds;
                var texture = source.PreviewTexture as Texture2D;
                Require(texture != null, "Preview is the completed captured image.");
                report.topLeft = texture.GetPixel(texture.width / 4, texture.height * 3 / 4);
                report.topRight = texture.GetPixel(texture.width * 3 / 4, texture.height * 3 / 4);
                report.bottomLeft = texture.GetPixel(texture.width / 4, texture.height / 4);
                report.bottomRight = texture.GetPixel(texture.width * 3 / 4, texture.height / 4);
                SaveObservation(result); // Keep the evidence even if orientation fails.
                Require(Matches(report.topLeft, Color.red) && Matches(report.topRight, Color.green) &&
                    Matches(report.bottomLeft, Color.blue) && Matches(report.bottomRight, Color.yellow),
                    "RGB orientation or self-head isolation failed; inspect saved color landmark image.");
                CheckUntouched();
                source.transform.position += Vector3.right * .3f;
                Require(!source.IsCurrentViewCompatible(result), "Translation expires view compatibility.");
                source.transform.position -= Vector3.right * .3f;
                var originalScene = source.gameObject.scene;
                var otherScene = SceneManager.CreateScene("Vision compatibility isolated transfer");
                Require(!source.IsCurrentViewCompatible(result), "Additive scene changes expire compatibility without moving the avatar.");
                SceneManager.MoveGameObjectToScene(source.gameObject, otherScene);
                Require(!source.IsCurrentViewCompatible(result), "Scene transfer expires compatibility at the same pose.");
                SceneManager.MoveGameObjectToScene(source.gameObject, originalScene);
                SceneManager.UnloadSceneAsync(otherScene);
                source.SetObserving(false);
                Require(!source.LatestObservation.HasImage && source.PreviewTexture == null && !source.IsCurrentViewCompatible(result), "Closing clears current pixels and compatibility.");
                source.SetObserving(true);
                Require(!source.IsCurrentViewCompatible(result), "Reopening does not revive old observation identity.");
                phase = 1; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 1 && Time.realtimeSinceStartup - phaseAt > .6f)
            {
                // Invoke this one production frame synchronously so cancellation runs after
                // GPU submission and before its asynchronous completion, without timing luck.
                source.RequestCapture(value => { report.cancelCallbacks++; Require(!value.HasImage, "Cancelled callback must contain no old pixels."); });
                typeof(NevaSceneVisionCapture).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null);
                report.renderedInFlight = typeof(NevaSceneVisionCapture).GetField("inFlight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source) != null;
                Require(report.renderedInFlight, "Cancellation fixture submitted a real GPU readback.");
                source.SetObserving(false);
                report.closedCleared = !source.LatestObservation.HasImage && source.PreviewTexture == null;
                Require(report.cancelCallbacks == 1 && report.closedCleared, "In-flight cancellation completes once and clears pixels immediately.");
                phase = 2; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 2 && Time.realtimeSinceStartup - phaseAt > .5f)
            {
                Require(report.cancelCallbacks == 1 && !source.LatestObservation.HasImage && source.PreviewTexture == null, "Late GPU completion cannot restore pixels or repeat callback.");
                CheckUntouched();
                source.SetObserving(true);
                int callbacks = 0;
                source.RequestCapture(value => { callbacks++; Require(!value.HasImage, "Queued disable contains no pixels."); });
                source.enabled = false;
                Require(callbacks == 1 && !source.IsObserving && !source.IsCapturePending && source.PreviewTexture == null, "Disable completes queued request and closes source.");
                Finish(null);
            }
        }
        catch (Exception e) { Finish(e is TargetInvocationException && e.InnerException != null ? e.InnerException : e); }
    }

    private static void CheckUntouched()
    {
        for (int i = 0; i < originalRenderers.Length; i++)
            Require(originalRenderers[i] != null && originalRenderers[i].forceRenderingOff == originalForceOff[i] &&
                originalRenderers[i].enabled == originalEnabled[i] && originalRenderers[i].gameObject.layer == originalLayers[i], "Original renderer state restored: " + i);
        foreach (var renderer in source.Target.GetComponentsInChildren<Renderer>(true))
            if (renderer.name == "NeEEvA scene vision headless proxy") Require(!renderer.enabled, "Headless proxy is disabled outside its render.");
        Require(userCamera.transform.position == userPosition && userCamera.transform.rotation == userRotation &&
            userCamera.cullingMask == userMask && userCamera.enabled == userEnabled && userCamera.fieldOfView == 53 && userCamera.targetTexture == null,
            "User camera properties remain unchanged.");
        report.rendererRestored = true; report.cameraUnchanged = true;
    }
    private static void SaveObservation(SceneVisionObservation observation)
    {
        string output = SessionState.GetString(Key + "Output", ""); Directory.CreateDirectory(output);
        string encoded = observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',') + 1);
        File.WriteAllBytes(Path.Combine(output, "color-landmarks.jpg"), Convert.FromBase64String(encoded));
        File.WriteAllText(Path.Combine(output, "color-landmarks.metadata.json"), observation.MetadataJson);
    }
    private static bool Matches(Color actual, Color expected) => Mathf.Abs(actual.r - expected.r) < .15f &&
        Mathf.Abs(actual.g - expected.g) < .15f && Mathf.Abs(actual.b - expected.b) < .15f;
    private static void Require(bool condition, string message)
    { if (report != null) report.checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        if (source != null) source.SetObserving(false);
        string output = SessionState.GetString(Key + "Output", "Logs/neva-scene-vision-capture"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "capture-regression.json"), JsonUtility.ToJson(report, true));
        SessionState.SetBool(Key + "Finished", true); SessionState.SetInt(Key + "Exit", error == null ? 0 : 1);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    private static void Exit()
    {
        SessionState.SetBool(Key + "Active", false);
        EditorApplication.Exit(SessionState.GetInt(Key + "Exit", 1));
    }
    private static string Argument(string key, string fallback)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length - 1; i++) if (arguments[i] == key) return arguments[i + 1];
        return fallback;
    }
}
