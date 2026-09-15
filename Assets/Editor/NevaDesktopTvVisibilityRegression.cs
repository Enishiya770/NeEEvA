using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>Real installed TV Quad and production shader, synthetic pixels only. Never enters Play.</summary>
public static class NevaDesktopTvVisibilityRegression
{
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    [Serializable] private sealed class View
    {
        public string name;
        public Vector3 cameraPosition, forward;
        public Color topLeft, topRight, bottomLeft, bottomRight;
        public int coloredPixels;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, graphicsApi, sceneHashBefore, sceneHashAfter, shaderHash, materialPath, materialName, shaderName;
        public string scope = "Existing copied Chat scene installed Desktop Screen Quad with production DesktopScreen shader and a generated public four-color texture. All scene behaviors/renderers disabled except the tested screen; no Play, desktop capture, model request or source scene save. Front/back checks isolate the screen itself so the TV housing cannot conceal a double-sided shader defect; a controlled opaque blocker checks normal depth occlusion.";
        public int checks, sourceLayer, displayedDesktopFrames;
        public bool desktopTextureAbsent, playerModeEntered;
        public Vector3 quadLocalNormal, localTriangleCross, frontInFrame, frontWorld, screenPosition, screenScale;
        public Quaternion screenRotation;
        public float expectedNormalDot, transformDeterminant;
        public List<View> views = new List<View>();
    }
    private static Report report;
    private static string output;
    private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0 || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Only edit-mode graphics in the existing isolated validation project is permitted.");
        output = Path.GetFullPath(Argument("-nevaTvVisibilityOutput", "Logs/neva-tv-visibility"));
        Directory.CreateDirectory(output); report = new Report();
        try
        {
            Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "Graphics device required; do not pass -nographics.");
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.sceneHashBefore = Hash(ScenePath); report.shaderHash = Hash("Assets/AIChatTookit/Shaders/DesktopScreen.shader");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var transforms = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).ToArray();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true)) if (behaviour != null) behaviour.enabled = false;
                foreach (var existingRenderer in root.GetComponentsInChildren<Renderer>(true)) existingRenderer.enabled = false;
            }
            var screen = transforms.Single(t => t.name == DesktopTvSetup.ScreenName && t.parent != null && t.parent.name == "tv_frame");
            var display = screen.GetComponent<DesktopTvScreen>();
            var renderer = screen.GetComponent<MeshRenderer>();
            var mesh = screen.GetComponent<MeshFilter>().sharedMesh;
            report.desktopTextureAbsent = display != null && display.DesktopTexture == null;
            report.displayedDesktopFrames = display != null ? display.DisplayedFrames : -1;
            report.materialName = renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "missing";
            report.materialPath = renderer.sharedMaterial != null ? AssetDatabase.GetAssetPath(renderer.sharedMaterial) : "";
            report.shaderName = renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name : "missing";
            Require(display != null && !display.enabled && display.DesktopTexture == null && display.DisplayedFrames == 0, "Live desktop component stays disabled and has never supplied pixels.");
            Require(mesh != null && mesh.vertexCount == 4 && mesh.normals.Length == 4, "Actual installed screen uses the expected Quad mesh.");
            report.quadLocalNormal = mesh.normals[0];
            var vertices = mesh.vertices; var triangles = mesh.triangles;
            report.localTriangleCross = Vector3.Cross(vertices[triangles[1]] - vertices[triangles[0]], vertices[triangles[2]] - vertices[triangles[0]]).normalized;
            report.frontWorld = screen.localToWorldMatrix.inverse.transpose.MultiplyVector(report.quadLocalNormal).normalized;
            report.frontInFrame = screen.parent.InverseTransformDirection(report.frontWorld).normalized;
            report.expectedNormalDot = Vector3.Dot(report.frontWorld, screen.parent.TransformDirection(Vector3.left).normalized);
            report.screenPosition = screen.position; report.screenRotation = screen.rotation; report.screenScale = screen.lossyScale;
            report.transformDeterminant = screen.localToWorldMatrix.determinant; report.sourceLayer = screen.gameObject.layer;
            Require(Vector3.Dot(report.quadLocalNormal, Vector3.back) > .999f && report.expectedNormalDot > .999f,
                "The existing Quad normal -Z must face tv_frame local -X, preserving the user's authored TV front.");
            Require(report.transformDeterminant > 0, "Actual screen has positive winding parity; a mirrored fixture cannot prove this authored front.");
            Require(renderer.sharedMaterial != null && renderer.sharedMaterial.shader.name == "AIChatToolkit/DesktopScreen", "Actual TV material uses the production shader.");
            var texture = FourColors();
            var material = Own(new Material(renderer.sharedMaterial));
            material.SetTexture("_MainTex", texture);
            float aspect = screen.TransformVector(Vector3.right).magnitude / screen.TransformVector(Vector3.up).magnitude;
            material.SetFloat("_SourceAspect", aspect); material.SetFloat("_ScreenAspect", aspect);
            material.SetFloat("_FlipX", 0); material.SetFloat("_FlipY", 0); material.SetFloat("_Brightness", 1);
            renderer.sharedMaterial = material; renderer.SetPropertyBlock(null); renderer.enabled = true;
            var host = Own(new GameObject("Synthetic TV visibility camera"));
            var camera = host.AddComponent<Camera>(); camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black; camera.allowHDR = false; camera.allowMSAA = false;
            camera.orthographic = true; camera.orthographicSize = screen.TransformVector(Vector3.up).magnitude * .58f;
            camera.nearClipPlane = .02f; camera.farClipPlane = 20; camera.cullingMask = 1 << screen.gameObject.layer;
            float distance = Mathf.Max(1, screen.TransformVector(Vector3.up).magnitude);
            Vector3 up = screen.TransformVector(Vector3.up).normalized;
            camera.transform.SetPositionAndRotation(screen.position + report.frontWorld * distance, Quaternion.LookRotation(-report.frontWorld, up));
            View front = Capture(camera, screen, "front");
            Require(Matches(front.topLeft, Color.red) && Matches(front.topRight, Color.green) &&
                Matches(front.bottomLeft, Color.blue) && Matches(front.bottomRight, Color.yellow),
                "Authored front must show the complete correctly oriented synthetic TV image.");
            camera.transform.SetPositionAndRotation(screen.position - report.frontWorld * distance, Quaternion.LookRotation(report.frontWorld, up));
            View back = Capture(camera, screen, "back");
            Require(back.coloredPixels == 0, "The isolated screen back must expose no desktop pixels without relying on the TV housing.");
            camera.transform.SetPositionAndRotation(screen.position + report.frontWorld * distance, Quaternion.LookRotation(-report.frontWorld, up));
            var blocker = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); blocker.name = "Synthetic opaque left-half occluder";
            blocker.layer = screen.gameObject.layer; blocker.transform.SetParent(screen, false);
            blocker.transform.localPosition = new Vector3(-.25f, 0, -.03f); blocker.transform.localScale = new Vector3(.5f, 1, .01f);
            var black = Own(new Material(Shader.Find("Unlit/Color"))); black.color = Color.black;
            blocker.GetComponent<Renderer>().sharedMaterial = black;
            View occluded = Capture(camera, screen, "front-left-occluded");
            Require(Matches(occluded.topLeft, Color.black) && Matches(occluded.bottomLeft, Color.black) &&
                Matches(occluded.topRight, Color.green) && Matches(occluded.bottomRight, Color.yellow),
                "Opaque geometry must hide the covered pixels while preserving the unobstructed TV half.");
            report.desktopTextureAbsent = display.DesktopTexture == null;
            report.displayedDesktopFrames = display.DisplayedFrames; report.playerModeEntered = EditorApplication.isPlayingOrWillChangePlaymode;
            Require(report.desktopTextureAbsent && report.displayedDesktopFrames == 0 && !report.playerModeEntered, "No desktop feed or Play mode was used.");
            report.sceneHashAfter = Hash(ScenePath);
            Require(report.sceneHashAfter == report.sceneHashBefore, "Source scene bytes remain unchanged.");
            report.status = "passed";
        }
        catch (Exception e) { report.status = "failed"; report.error = e.ToString(); }
        finally
        {
            foreach (var item in owned.AsEnumerable().Reverse()) if (item != null) UnityEngine.Object.DestroyImmediate(item);
            owned.Clear();
            if (File.Exists(ScenePath)) report.sceneHashAfter = Hash(ScenePath);
            File.WriteAllText(Path.Combine(output, "tv-visibility.json"), JsonUtility.ToJson(report, true));
        }
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }

    private static View Capture(Camera camera, Transform screen, string name)
    {
        const int width = 384, height = 256;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(width, height, TextureFormat.RGBA32, false);
        RenderTexture previous = RenderTexture.active;
        try
        {
            target.Create(); camera.targetTexture = target; camera.aspect = (float)width / height; camera.Render();
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); pixels.Apply(false, false);
            var view = new View { name = name, cameraPosition = camera.transform.position, forward = camera.transform.forward,
                topLeft = At(pixels, camera, screen, -.25f, .25f), topRight = At(pixels, camera, screen, .25f, .25f),
                bottomLeft = At(pixels, camera, screen, -.25f, -.25f), bottomRight = At(pixels, camera, screen, .25f, -.25f) };
            foreach (Color32 pixel in pixels.GetPixels32()) if (pixel.r > 20 || pixel.g > 20 || pixel.b > 20) view.coloredPixels++;
            report.views.Add(view); File.WriteAllBytes(Path.Combine(output, name + ".png"), pixels.EncodeToPNG()); return view;
        }
        finally
        { camera.targetTexture = null; RenderTexture.active = previous; target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels); }
    }
    private static Color At(Texture2D pixels, Camera camera, Transform screen, float x, float y)
    {
        Vector3 uv = camera.WorldToViewportPoint(screen.TransformPoint(new Vector3(x, y, 0)));
        return pixels.GetPixel(Mathf.Clamp(Mathf.RoundToInt(uv.x * pixels.width), 0, pixels.width - 1), Mathf.Clamp(Mathf.RoundToInt(uv.y * pixels.height), 0, pixels.height - 1));
    }
    private static Texture2D FourColors()
    {
        var texture = Own(new Texture2D(64, 64, TextureFormat.RGBA32, false));
        texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            texture.SetPixel(x, y, y >= 32 ? (x < 32 ? Color.red : Color.green) : (x < 32 ? Color.blue : Color.yellow));
        texture.Apply(false, false); return texture;
    }
    private static T Own<T>(T item) where T : UnityEngine.Object { owned.Add(item); return item; }
    private static bool Matches(Color a, Color b) => Mathf.Abs(a.r - b.r) < .15f && Mathf.Abs(a.g - b.g) < .15f && Mathf.Abs(a.b - b.b) < .15f;
    private static void Require(bool value, string reason) { report.checks++; if (!value) throw new InvalidOperationException(reason); }
    private static string Hash(string path) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
    private static string Argument(string key, string fallback) { string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
}
