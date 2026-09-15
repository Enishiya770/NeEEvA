using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Perception;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Real GPU pixel/depth association, no collider/world-coordinate fallback. Isolated project only.</summary>
[InitializeOnLoad]
public static class SceneVisionGroundingRegression
{
    private const string Key = "NeEEvA.SceneVisionGroundingRegression.";
    [Serializable] private sealed class Report
    {
        public string status = "running", error, graphics, scope = "Actual character RGB + same-frame GPU surface IDs/depth; real VRM, collider-free opaque panels/TV, occlusion, unsupported transparent material, stale/lifecycle/geometry fences. No model semantic or navigation claim.";
        public int checks, captures;
        public Vector3 topLeftWorld, bottomLeftWorld, tvWorld;
        public string captureId;
    }
    private static Report report;
    private static NevaSceneVisionCapture source;
    private static SceneVisionObservation observation;
    private static SceneGroundedTarget television;
    private static MeshRenderer red, blue, tv, cover;
    private static Pose eye;
    private static int phase;
    private static float expiryAt;

    static SceneVisionGroundingRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                try { Initialize(); } catch (Exception error) { Finish(error); }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Grounding regression only runs in the existing isolated validation project.");
        SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-nevaSceneVisionOutput", "Logs/neva-grounding")));
        try
        {
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var actor = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NeEEvA.vrm"), scene);
            actor.name = "Grounding isolated avatar";
            foreach (Behaviour component in actor.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
            var capture = actor.AddComponent<NevaSceneVisionCapture>(); capture.Configure(actor.GetComponentInChildren<Vrm10Instance>(true));
            if (!capture.TryGetCurrentCameraPose(out var pose)) throw new InvalidOperationException("Neutral camera unavailable.");
            Panel("Ground red", pose, new Vector3(-.8f, .5f, 2), Color.red);
            Panel("Ground blue", pose, new Vector3(-.8f, -.5f, 2), Color.blue);
            Panel("Ground green", pose, new Vector3(.8f, .5f, 2), Color.green);
            var glass = Panel("Ground glass", pose, new Vector3(.8f, -.5f, 2), Color.yellow);
            glass.sharedMaterial = new Material(Shader.Find("Standard"));
            glass.sharedMaterial.SetFloat("_Mode", 3); glass.sharedMaterial.renderQueue = 3000;
            var television = Panel("Ground TV", pose, new Vector3(0, 0, 2.5f), Color.white);
            television.sharedMaterial = new Material(Shader.Find("AIChatToolkit/DesktopScreen"));
            var occluder = Panel("Ground occluder", pose, new Vector3(0, 0, 2), Color.magenta); occluder.enabled = false;
            // Behind the red pixel: resolving it must give the foreground panel depth, not a convenient farther mesh.
            Panel("Ground behind red", pose, new Vector3(-1.2f, .75f, 3), Color.cyan);
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 55);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Finish(error); }
    }

    private static MeshRenderer Panel(string name, Pose pose, Vector3 local, Color color)
    {
        var host = GameObject.CreatePrimitive(PrimitiveType.Quad); host.name = name;
        UnityEngine.Object.DestroyImmediate(host.GetComponent<Collider>());
        host.transform.SetPositionAndRotation(pose.position + pose.rotation * local, pose.rotation);
        host.transform.localScale = new Vector3(.68f, .65f, 1);
        var renderer = host.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = color }; return renderer;
    }
    private static void Initialize()
    {
        report = new Report { graphics = SystemInfo.graphicsDeviceType.ToString() };
        source = GameObject.Find("Grounding isolated avatar").GetComponent<NevaSceneVisionCapture>();
        red = GameObject.Find("Ground red").GetComponent<MeshRenderer>();
        blue = GameObject.Find("Ground blue").GetComponent<MeshRenderer>();
        tv = GameObject.Find("Ground TV").GetComponent<MeshRenderer>();
        cover = GameObject.Find("Ground occluder").GetComponent<MeshRenderer>();
        Require(source.TryGetCurrentCameraPose(out eye), "Actual calibrated avatar view available.");
        Require(tv.GetComponent<Collider>() == null, "TV fixture has no collider.");
        source.SetObserving(true); Capture();
    }
    private static void Capture() { observation = null; source.RequestCapture(value => { report.captures++; observation = value; }); }
    private static Vector2 Pixel(Vector3 world)
    {
        var local = Quaternion.Inverse(eye.rotation) * (world - eye.position);
        float tangent = Mathf.Tan(source.verticalFieldOfView * Mathf.Deg2Rad * .5f);
        return new Vector2(.5f + local.x / (2 * local.z * tangent * source.captureWidth / source.captureHeight), .5f - local.y / (2 * local.z * tangent));
    }
    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("Grounding regression timeout.");
            if (!EditorApplication.isPlaying || report == null || observation == null) return;
            if (phase == 3 && Time.realtimeSinceStartup < expiryAt) return;
            Require(observation.HasImage, "RGB remains available: " + observation.Reason);
            Require(observation.HasSurfaceGrounding, "Companion buffer available: " + observation.SurfaceGroundingReason);
            if (phase == 0)
            {
                report.captureId = observation.CaptureId;
                var output = SessionState.GetString(Key + "Output", ""); Directory.CreateDirectory(output);
                File.WriteAllBytes(Path.Combine(output, "grounding-rgb.jpg"), Convert.FromBase64String(observation.ImageDataUrl.Split(',')[1]));
                SaveBufferDiagnostic(observation, output);
                Require(source.TryResolve(observation, Pixel(red.transform.position), out var redTarget, out var reason), "Resolve upper-left red: " + reason);
                Require(source.TryResolve(observation, Pixel(blue.transform.position), out var blueTarget, out reason), "Resolve lower-left blue: " + reason);
                report.topLeftWorld = redTarget.WorldPoint; report.bottomLeftWorld = blueTarget.WorldPoint;
                Require(Vector3.Distance(redTarget.WorldPoint, red.transform.position) < .01f, "Captured red pixel projects to its physical surface, not behind it.");
                Require(Vector3.Distance(blueTarget.WorldPoint, blue.transform.position) < .01f, "Top-left image convention matches RGB and lower-left geometry.");
                Require(redTarget.SurfaceId != blueTarget.SurfaceId, "Distinct physical surface IDs.");
                Require(source.TryResolve(observation, Pixel(tv.transform.position), out television, out reason), "Resolve collider-free TV: " + reason);
                report.tvWorld = television.WorldPoint;
                Require(Vector3.Distance(television.WorldPoint, tv.transform.position) < .01f, "TV content grounds to the screen surface only.");
                Require(!source.TryResolve(observation, Pixel(GameObject.Find("Ground glass").transform.position), out _, out reason), "Transparent material is conservatively ungroundable.");
                Require(!source.TryResolve(observation, new Vector2(float.NaN, .5f), out _, out _), "Reject NaN pixel.");
                Require(!source.TryResolve(observation, new Vector2(1.1f, .5f), out _, out _), "Reject outside pixel.");
                var position = red.transform.position; red.transform.position += Vector3.up * .05f;
                Require(!redTarget.TryValidate(out _), "Captured target motion invalidates target.");
                Require(!source.TryResolve(observation, Pixel(position), out _, out _), "Old image cannot resolve moved geometry.");
                red.transform.position = position;
                var mesh = red.GetComponent<MeshFilter>().sharedMesh;
                red.GetComponent<MeshFilter>().sharedMesh = null;
                Require(!redTarget.TryValidate(out _), "Replaced/removed mesh invalidates target.");
                red.GetComponent<MeshFilter>().sharedMesh = mesh;
                int queue = tv.sharedMaterial.renderQueue; tv.sharedMaterial.renderQueue = 3000;
                Require(!television.TryValidateGeometry(out _), "Rendering mode change invalidates committed geometry too.");
                tv.sharedMaterial.renderQueue = queue;
                var rotation = source.Target.transform.rotation;
                source.Target.transform.rotation = Quaternion.AngleAxis(35, Vector3.up) * rotation;
                Require(!source.TryResolve(observation, Pixel(tv.transform.position), out _, out _), "Changed current view rejects stale pixel binding.");
                source.Target.transform.rotation = rotation;
                Require(television.TryProject(observation, out var projected, out reason) && Vector2.Distance(projected, television.ImagePoint) < .002f, "Reprojection is consistent with sampled pixel: " + reason);
                UnityEngine.Object.DestroyImmediate(red.gameObject);
                Require(!redTarget.TryValidateGeometry(out _), "Destroyed surface cannot remain a committed target.");
                cover.enabled = true; phase = 1; Capture();
            }
            else if (phase == 1)
            {
                Require(!source.TryConfirmVisible(observation, television, out _, out _), "Fresh nearer occluder prevents renewing old TV visibility.");
                Require(source.TryResolve(observation, Pixel(tv.transform.position), out var blocking, out var reason), "New frame resolves the occluder: " + reason);
                Require(blocking.SurfaceId != television.SurfaceId && Vector3.Distance(blocking.WorldPoint, cover.transform.position) < .01f, "Nearest visible surface wins.");
                cover.enabled = false; phase = 2; Capture();
            }
            else if (phase == 2)
            {
                Require(source.TryConfirmVisible(observation, television, out var renewed, out var reason), "New unobstructed frame renews same target: " + reason);
                Require(renewed.CaptureId != television.CaptureId && renewed.SurfaceId == television.SurfaceId, "Renewal carries new capture ID and stable surface identity.");
                television = renewed; phase = 3;
                expiryAt = observation.CapturedAtRealtime + SceneVisionGrounding.MaximumObservationAge + .1f;
            }
            else if (phase == 3)
            {
                Require(!television.TryValidate(out _), "Expired observation cannot start a new action.");
                Require(television.TryValidateGeometry(out var reason), "Already committed unchanged geometry is not aborted by image age alone: " + reason);
                Require(!source.TryResolve(observation, Pixel(tv.transform.position), out _, out _), "Expired capture cannot resolve a new target.");
                phase = 4; Capture();
            }
            else if (phase == 4)
            {
                Require(source.TryConfirmVisible(observation, television, out var renewed, out var reason), "Fresh image can renew unchanged geometry after old evidence expires: " + reason);
                source.SetObserving(false);
                Require(!renewed.TryValidate(out _) && !renewed.TryValidateGeometry(out _), "Closing eyes clears source ownership including committed target.");
                Require(!source.TryResolve(observation, Pixel(tv.transform.position), out _, out _), "Closed source cannot reuse previous pixels.");
                Finish(null);
            }
        }
        catch (Exception error) { Finish(error); }
    }
    private static void Require(bool pass, string message) { report.checks++; if (!pass) throw new InvalidOperationException(message); }
    private static void SaveBufferDiagnostic(SceneVisionObservation value, string output)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object frame = typeof(SceneVisionObservation).GetProperty("Grounding", flags).GetValue(value);
        object Field(object owner, string name) => owner.GetType().GetField(name, flags).GetValue(owner);
        int width = (int)Field(frame, "width"), height = (int)Field(frame, "height");
        bool topFirst = (bool)Field(frame, "rowsStartAtTop");
        float[] samples = (float[])Field(frame, "depthIds"); var entries = (Array)Field(frame, "entries");
        var objects = new JArray(); var pixels = new Color[width * height];
        for (int id = -1; id <= entries.Length; id++)
        {
            int count = 0, minX = width, minY = height, maxX = -1, maxY = -1;
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            for (int i = 0; i < width * height; i++) if (Mathf.RoundToInt(samples[i * 2 + 1]) == id)
            {
                count++; int x = i % width, y = i / width;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                low = Mathf.Min(low, samples[i * 2]); high = Mathf.Max(high, samples[i * 2]);
                pixels[i] = id == 0 ? Color.black : id < 0 ? Color.red : Color.HSVToRGB((id * .137f) % 1f, .8f, 1f);
            }
            object entry = id > 0 ? entries.GetValue(id - 1) : null;
            var renderer = entry != null ? (Renderer)Field(entry, "renderer") : null;
            objects.Add(new JObject { ["id"] = id, ["fixtureRenderer"] = renderer != null ? renderer.name : "background/blocker",
                ["count"] = count, ["minX"] = minX, ["maxX"] = maxX, ["minYRaw"] = minY, ["maxYRaw"] = maxY,
                ["minDepth"] = count > 0 ? low : 0, ["maxDepth"] = count > 0 ? high : 0,
                ["shader"] = renderer != null ? string.Join(",", renderer.sharedMaterials.Select(x => x?.shader?.name ?? "null")) : "" });
        }
        var centers = new JArray();
        foreach (Renderer renderer in new Renderer[] { red, blue, tv })
        {
            Vector2 p = Pixel(renderer.transform.position);
            int x = Mathf.Clamp((int)(p.x * width), 0, width - 1), y = Mathf.Clamp((int)((topFirst ? p.y : 1-p.y)*height), 0, height-1);
            int offset = (y * width + x) * 2;
            centers.Add(new JObject { ["fixture"] = renderer.name, ["x"] = p.x, ["y"] = p.y, ["depth"] = samples[offset], ["id"] = samples[offset+1] });
        }
        File.WriteAllText(Path.Combine(output, "grounding-buffer.json"), new JObject { ["width"] = width, ["height"] = height,
            ["rowsStartAtTop"] = topFirst, ["reversedZ"] = SystemInfo.usesReversedZBuffer, ["entries"] = objects, ["centers"] = centers }.ToString());
        if (topFirst)
            for (int y = 0; y < height / 2; y++) for (int x = 0; x < width; x++)
            { int a = y * width + x, b = (height - y - 1) * width + x; var c = pixels[a]; pixels[a] = pixels[b]; pixels[b] = c; }
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        try { texture.SetPixels(pixels); texture.Apply(); File.WriteAllBytes(Path.Combine(output, "grounding-buffer.png"), texture.EncodeToPNG()); }
        finally { UnityEngine.Object.DestroyImmediate(texture); }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        if (report == null) report = new Report(); report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        if (source != null) source.SetObserving(false);
        var output = SessionState.GetString(Key + "Output", "Logs/neva-grounding"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "grounding-regression.json"), JsonUtility.ToJson(report, true));
        SessionState.SetBool(Key + "Finished", true); SessionState.SetInt(Key + "Exit", error == null ? 0 : 1);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    private static void Exit() { SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(SessionState.GetInt(Key + "Exit", 1)); }
    private static string Argument(string key, string fallback)
    { var args = Environment.GetCommandLineArgs(); for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1]; return fallback; }
}
