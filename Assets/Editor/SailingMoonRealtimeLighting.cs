using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Additive, reversible fill lighting over the room's existing lightmaps.</summary>
public static class SailingMoonRealtimeLighting
{
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string RigName = "[NeEEvA Realtime Fill - No Bake]";
    private const string OutputFolder = "LocalPrivate/SailingMoonRealtimeLighting";

    [MenuItem("NeEEvA/Private Rooms/Apply Realtime Fill (No Bake)")]
    public static void ApplyToOpenScene()
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != ScenePath)
            throw new InvalidOperationException("Open the SailingMoon Day Chat scene in Edit mode first.");
        Apply(scene);
        EditorSceneManager.MarkSceneDirty(scene);
    }

    [MenuItem("NeEEvA/Private Rooms/Toggle Realtime Fill")]
    public static void Toggle()
    {
        var scene = SceneManager.GetActiveScene();
        var rig = scene.GetRootGameObjects().FirstOrDefault(root => root.name == RigName);
        if (rig == null) return;
        Undo.RecordObject(rig, "Toggle realtime fill");
        rig.SetActive(!rig.activeSelf);
        EditorSceneManager.MarkSceneDirty(scene);
        SceneView.RepaintAll();
    }

    public static void ApplyAndCaptureBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch mode only.");
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var camera = FindCamera(scene);
            var lightmaps = LightmapSettings.lightmaps.Select(map => map.lightmapColor).ToArray();
            var dataAsset = Lightmapping.lightingDataAsset;
            // Rendering at an export resolution can rebuild UI layout in Edit mode.
            var canvasState = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<RectTransform>(true))
                .Select(rect => (rect, rect.anchorMin, rect.anchorMax, rect.anchoredPosition, rect.sizeDelta))
                .ToArray();
            var previousRig = scene.GetRootGameObjects().FirstOrDefault(root => root.name == RigName);
            if (previousRig != null) previousRig.SetActive(false);
            Capture(camera, "before.png");
            CaptureOverview(camera, "room-before.png");
            Apply(scene);
            Capture(camera, "after.png");
            CaptureOverview(camera, "room-after.png");
            var rig = scene.GetRootGameObjects().Single(root => root.name == RigName);
            var fillLights = rig.GetComponentsInChildren<Light>();
            var savedIntensities = fillLights.Select(light => light.intensity).ToArray();
            foreach (var light in fillLights) light.intensity *= 1.4f;
            Capture(camera, "brighter.png");
            CaptureOverview(camera, "room-brighter.png");
            for (var i = 0; i < fillLights.Length; i++) fillLights[i].intensity = savedIntensities[i];
            foreach (var state in canvasState)
            {
                state.rect.anchorMin = state.anchorMin;
                state.rect.anchorMax = state.anchorMax;
                state.rect.anchoredPosition = state.anchoredPosition;
                state.rect.sizeDelta = state.sizeDelta;
            }

            if (Lightmapping.lightingDataAsset != dataAsset ||
                !lightmaps.SequenceEqual(LightmapSettings.lightmaps.Select(map => map.lightmapColor)))
                throw new InvalidOperationException("Existing baked lighting changed unexpectedly.");
            if (fillLights.Any(light => light.lightmapBakeType != LightmapBakeType.Realtime ||
                                       light.shadows != LightShadows.None))
                throw new InvalidOperationException("Fill must use realtime lights without shadow maps.");

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Scene could not be saved.");
            var report = $"Scene: {scene.path}\nCamera: {camera.transform.position}, Euler: {camera.transform.eulerAngles}\n" +
                         $"Preserved lightmaps: {lightmaps.Length}\nRealtime fill lights: {fillLights.Length}\n" +
                         "No bake was requested. Existing lights, materials, probes and camera settings were preserved.\n";
            foreach (var light in fillLights)
                report += $"{light.name}: {light.type}, position={light.transform.position}, intensity={light.intensity}, range={light.range}\n";
            var renderers = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Renderer>(true));
            report += "Renderers with lightmap assignment: " + renderers.Count(renderer => renderer.lightmapIndex >= 0 && renderer.lightmapIndex < lightmaps.Length) + "\n";
            File.WriteAllText(Path.Combine(OutputFolder, "validation.txt"), report);
            Debug.Log("[SailingMoonRealtimeLighting] " + report);
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void Apply(Scene scene)
    {
        var rig = scene.GetRootGameObjects().FirstOrDefault(root => root.name == RigName);
        if (rig == null)
        {
            rig = new GameObject(RigName);
            SceneManager.MoveGameObjectToScene(rig, scene);
            Undo.RegisterCreatedObjectUndo(rig, "Add realtime fill");
        }
        Undo.RecordObject(rig, "Enable realtime fill");
        rig.SetActive(true);
        // These broad, shadowless sources approximate diffuse bounce. Keep the existing
        // baked sun and contact shadows, and avoid introducing another hard shadow.
        ConfigureLight(rig, "Living room warm bounce", LightType.Point,
            new Vector3(-2.8f, 1.35f, -6.0f), new Color(1f, 0.94f, 0.86f), 1.0f, 6.5f);
        ConfigureLight(rig, "Kitchen soft fill", LightType.Point,
            new Vector3(-8.3f, 1.5f, -4.8f), new Color(1f, 0.95f, 0.88f), 0.85f, 4.5f);

        var camera = FindCamera(scene);
        var portrait = ConfigureLight(rig, "Portrait soft fill", LightType.Spot,
            camera.transform.position + Vector3.up * 0.65f,
            new Color(1f, 0.97f, 0.93f), 0.8f, 4f);
        portrait.spotAngle = 95f;
        portrait.innerSpotAngle = 65f;
        portrait.transform.rotation = Quaternion.LookRotation(camera.transform.forward - Vector3.up * 0.18f);
        // Preserve the character's dedicated key when room fill is reapplied.
        var portraitLayer = LayerMask.NameToLayer("NevaPortrait");
        var portraitKeyEnabled = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Light>(true))
            .Any(light => light.name == "[NeEEvA Portrait Key - VRoid]" && light.isActiveAndEnabled);
        if (portraitLayer >= 0 && portraitKeyEnabled)
            foreach (var light in rig.GetComponentsInChildren<Light>(true))
                light.cullingMask &= ~(1 << portraitLayer);
        Selection.activeGameObject = rig;
        SceneView.RepaintAll();
    }

    private static Light ConfigureLight(GameObject rig, string name, LightType type,
        Vector3 position, Color color, float intensity, float range)
    {
        var child = rig.transform.Find(name);
        if (child == null)
        {
            child = new GameObject(name).transform;
            child.SetParent(rig.transform, false);
            Undo.RegisterCreatedObjectUndo(child.gameObject, "Add fill light");
        }
        var light = child.GetComponent<Light>();
        if (light == null) light = Undo.AddComponent<Light>(child.gameObject);
        Undo.RecordObject(light, "Configure fill light");
        Undo.RecordObject(child, "Position fill light");
        child.position = position;
        light.type = type;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.lightmapBakeType = LightmapBakeType.Realtime;
        light.shadows = LightShadows.None;
        light.bounceIntensity = 0f;
        light.renderMode = LightRenderMode.ForcePixel;
        light.cullingMask = ~0;
        light.enabled = true;
        return light;
    }

    private static Camera FindCamera(Scene scene)
    {
        return scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .First(camera => camera.gameObject.name == "Camera");
    }

    private static void CaptureOverview(Camera camera, string name)
    {
        var position = camera.transform.localPosition;
        var rotation = camera.transform.localRotation;
        try
        {
            camera.transform.position = new Vector3(-0.9f, 0.65f, -6.3f);
            camera.transform.LookAt(new Vector3(-5f, 1.15f, -6.0f));
            Capture(camera, name);
        }
        finally
        {
            camera.transform.localPosition = position;
            camera.transform.localRotation = rotation;
        }
    }

    private static void Capture(Camera camera, string name)
    {
        Directory.CreateDirectory(OutputFolder);
        const int width = 1600;
        const int height = 1100;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 2 };
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        var previousAspect = camera.aspect;
        try
        {
            camera.targetTexture = target;
            camera.aspect = (float)width / height;
            camera.Render();
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(Path.Combine(OutputFolder, name), texture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            camera.aspect = previousAspect;
            RenderTexture.active = previousActive;
            UnityEngine.Object.DestroyImmediate(texture);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
