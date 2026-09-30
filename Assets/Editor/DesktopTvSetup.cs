using System;
using System.Linq;
using NeEEvA.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class DesktopTvSetup
{
    public const string ScreenName = "Desktop Screen";
    public const string MaterialPath = "Assets/AIChatTookit/Materials/DesktopScreen.mat";
    private const string ConfigureMenuPath = "NeEEvA/TV/Show Desktop on Selected tv_frame";

    [MenuItem(ConfigureMenuPath)]
    public static void ConfigureSelected()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Configure the TV in Edit Mode, then enter Play Mode.");
        var selected = Selection.activeGameObject;
        if (selected == null || selected.GetComponent<MeshFilter>() == null)
            throw new InvalidOperationException("Select the tv_frame object in the Hierarchy.");
        var screen = ConfigureFrame(selected, true);
        Selection.activeGameObject = screen.gameObject;
        EditorSceneManager.MarkSceneDirty(selected.scene);
    }

    [MenuItem(ConfigureMenuPath, true)]
    private static bool ValidateConfigureSelected()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
        var selected = Selection.activeGameObject;
        return selected != null && string.Equals(selected.name, "tv_frame", StringComparison.Ordinal) &&
            selected.GetComponent<MeshFilter>()?.sharedMesh != null &&
            selected.GetComponent<MeshRenderer>() != null;
    }

    public static DesktopTvScreen ConfigureNamedFrame(Scene scene)
    {
        var frames = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Where(t => t.name == "tv_frame").ToArray();
        if (frames.Length != 1) throw new InvalidOperationException("Expected one object named tv_frame in " + scene.path);
        return ConfigureFrame(frames[0].gameObject, false);
    }

    public static DesktopTvScreen ConfigureFrame(GameObject frame, bool undo)
    {
        if (EditorUtility.IsPersistent(frame)) throw new InvalidOperationException("Select a scene instance of tv_frame.");
        if (!string.Equals(frame.name, "tv_frame", StringComparison.Ordinal))
            throw new InvalidOperationException("Select the scene object named tv_frame, not a duplicate or its child.");
        var mesh = frame.GetComponent<MeshFilter>()?.sharedMesh;
        var renderer = frame.GetComponent<MeshRenderer>();
        if (mesh == null || renderer == null)
            throw new InvalidOperationException("The selected tv_frame must have a MeshFilter and MeshRenderer.");
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null) throw new InvalidOperationException("DesktopScreen material is missing.");
        var child = frame.transform.Find(ScreenName);
        GameObject surface;
        if (child == null)
        {
            surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
            surface.name = ScreenName;
            if (undo) Undo.RegisterCreatedObjectUndo(surface, "Add desktop TV screen");
            surface.transform.SetParent(frame.transform, false);
            UnityEngine.Object.DestroyImmediate(surface.GetComponent<Collider>());
        }
        else surface = child.gameObject;
        if (undo) Undo.RecordObject(surface.transform, "Fit desktop screen");
        // Face -X, right +Y, up +Z in imported mesh coordinates. Keep the 5 cm bezel visible.
        surface.transform.localPosition = new Vector3(-0.00575f, 0f, 2.05f);
        surface.transform.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.forward);
        surface.transform.localScale = new Vector3(3.88f, 2.58f, 1f);
        surface.isStatic = false;
        var screenRenderer = surface.GetComponent<MeshRenderer>();
        if (screenRenderer == null)
            throw new InvalidOperationException("The Desktop Screen child must have a MeshRenderer.");
        if (undo) Undo.RecordObject(screenRenderer, "Set desktop screen material");
        screenRenderer.sharedMaterial = material;
        screenRenderer.shadowCastingMode = ShadowCastingMode.Off;
        screenRenderer.receiveShadows = false;
        screenRenderer.lightProbeUsage = LightProbeUsage.Off;
        screenRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        var display = surface.GetComponent<DesktopTvScreen>();
        if (display == null) display = undo ? Undo.AddComponent<DesktopTvScreen>(surface) : surface.AddComponent<DesktopTvScreen>();
        if (undo) Undo.RecordObject(display, "Fit desktop aspect ratio");
        display.screenAspect = surface.transform.TransformVector(Vector3.right).magnitude /
            surface.transform.TransformVector(Vector3.up).magnitude;
        return display;
    }
}

[CustomEditor(typeof(DesktopTvScreen))]
public sealed class DesktopTvScreenEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var display = (DesktopTvScreen)target;
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(display.Status, MessageType.Info);
        if (Application.isPlaying)
        {
            EditorGUILayout.LabelField("Displayed frames", display.DisplayedFrames.ToString());
            EditorGUILayout.HelpBox(display.ExclusionStatus, display.ExclusionHasError ? MessageType.Warning : MessageType.Info);
        }
        EditorGUILayout.HelpBox("Exclude Unity Windows / 仅在电视中排除 Unity：优先使用电视专用过滤；显卡驱动不兼容时按 Fallback Behavior 选择全局排除，或让 OBS/截图继续看到 Unity（电视可能出现镜像）。运行时按 F8 可切换所有正在采集的电视；也可单独勾选每台电视。", MessageType.Info);
    }
}

[InitializeOnLoad]
internal static class DesktopTvCaptureLifecycle
{
    static DesktopTvCaptureLifecycle()
    {
        AssemblyReloadEvents.beforeAssemblyReload += DesktopTvScreen.RestoreCaptureExclusion;
        EditorApplication.quitting += DesktopTvScreen.RestoreCaptureExclusion;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) DesktopTvScreen.RestoreCaptureExclusion();
        };
    }
}
