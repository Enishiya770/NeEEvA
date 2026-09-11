using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniVRM10;

/// <summary>A neutral key light restricted to the character, over the existing room lighting.</summary>
public static class NevaPortraitLighting
{
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string LightName = "[NeEEvA Portrait Key - VRoid]";
    private const string LayerName = "NevaPortrait";
    private const string Output = "LocalPrivate/NevaPortrait";

    [MenuItem("NeEEvA/Private Rooms/Apply VRoid Portrait (No Bake)")]
    public static void ApplyAndCapture()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Apply portrait lighting in Edit mode.");
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new InvalidOperationException("Open the SailingMoon Day Chat scene first.");
        Directory.CreateDirectory(Output);
        var avatar = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Vrm10Instance>(true))
            .Single(instance => instance.name == "NEVA");
        var camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .First(value => value.name == "Camera");
        var data = Lightmapping.lightingDataAsset;
        var maps = LightmapSettings.lightmaps.Select(map => map.lightmapColor).ToArray();
        var rectangles = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<RectTransform>(true))
            .Select(rect => (rect, rect.anchorMin, rect.anchorMax, rect.anchoredPosition, rect.sizeDelta)).ToArray();
        var existing = avatar.transform.Find(LightName);
        if (existing != null) existing.gameObject.SetActive(false);
        SetRoomFillContribution(scene, true);
        Capture(camera, avatar.transform, "before.png", true);
        Capture(camera, avatar.transform, "before-wide.png", false);
        var layer = GetPortraitLayer();
        var renderers = avatar.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            Undo.RecordObject(renderer.gameObject, "Set portrait light layer");
            renderer.gameObject.layer = layer;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer.gameObject);
        }
        if (existing == null)
        {
            existing = new GameObject(LightName).transform;
            existing.SetParent(avatar.transform, false);
            Undo.RegisterCreatedObjectUndo(existing.gameObject, "Add portrait key light");
        }
        existing.gameObject.SetActive(true);
        var light = existing.GetComponent<Light>();
        if (light == null) light = Undo.AddComponent<Light>(existing.gameObject);
        Undo.RecordObject(light, "Configure portrait light");
        light.type = LightType.Directional;
        light.lightmapBakeType = LightmapBakeType.Realtime;
        light.shadows = LightShadows.None;
        light.bounceIntensity = 0f;
        light.renderMode = LightRenderMode.ForcePixel;
        light.color = Color.white;
        light.cullingMask = 1 << layer;
        light.enabled = true;
        SetRoomFillContribution(scene, false);
        existing.localPosition = new Vector3(0, 1.7f, 1f);
        existing.localRotation = Quaternion.LookRotation(new Vector3(-0.15f, -0.45f, -1f));
        foreach (var intensity in new[] { 0.85f, 0.9f, 1.0f })
        {
            light.intensity = intensity;
            Capture(camera, avatar.transform, "portrait-" + intensity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ".png", true);
        }
        light.intensity = 0.9f;
        Capture(camera, avatar.transform, "after.png", true);
        Capture(camera, avatar.transform, "after-wide.png", false);
        foreach (var state in rectangles)
        {
            state.rect.anchorMin = state.anchorMin;
            state.rect.anchorMax = state.anchorMax;
            state.rect.anchoredPosition = state.anchoredPosition;
            state.rect.sizeDelta = state.sizeDelta;
        }
        if (data != Lightmapping.lightingDataAsset || !maps.SequenceEqual(LightmapSettings.lightmaps.Select(map => map.lightmapColor)))
            throw new InvalidOperationException("Existing baked lighting changed unexpectedly.");
        var otherRenderers = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Renderer>(true))
            .Where(renderer => !renderer.transform.IsChildOf(avatar.transform) && renderer.gameObject.layer == layer).ToArray();
        if (otherRenderers.Length != 0) throw new InvalidOperationException("Portrait layer is used by environment geometry.");
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save portrait lighting.");
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = existing.gameObject;
        SceneView.RepaintAll();
        File.WriteAllText(Path.Combine(Output, "validation.txt"),
            $"Avatar renderers: {renderers.Length}\nPortrait-only layer: {layer}\nOther renderers on layer: {otherRenderers.Length}\n" +
            $"Portrait key: Realtime Directional, white, intensity={light.intensity}, no shadows\n" +
            $"Preserved lightmaps: {maps.Length}\nMaterials and textures unchanged. No bake invoked.\n");
        Debug.Log("[NevaPortraitLighting] Portrait lighting saved. Before/after previews: " + Path.GetFullPath(Output));
    }

    [MenuItem("NeEEvA/Private Rooms/Toggle VRoid Portrait")]
    public static void Toggle()
    {
        var light = Resources.FindObjectsOfTypeAll<Light>().FirstOrDefault(value => value.name == LightName && value.gameObject.scene.IsValid());
        if (light == null) return;
        Undo.RecordObject(light.gameObject, "Toggle portrait key");
        light.gameObject.SetActive(!light.gameObject.activeSelf);
        SetRoomFillContribution(light.gameObject.scene, !light.gameObject.activeSelf);
        if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(light.gameObject.scene);
        SceneView.RepaintAll();
    }

    [MenuItem("NeEEvA/Private Rooms/Capture Portrait Comparison")]
    public static void CaptureComparison()
    {
        var scene = SceneManager.GetActiveScene();
        var avatar = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Vrm10Instance>(true))
            .Single(instance => instance.name == "NEVA");
        var camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .First(value => value.name == "Camera");
        var key = avatar.transform.Find(LightName);
        if (key == null) throw new InvalidOperationException("Apply portrait lighting first.");
        var active = key.gameObject.activeSelf;
        var masks = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Light>(true))
            .Select(light => (light, light.cullingMask)).ToArray();
        var prefix = Application.isPlaying ? "live-" : "preview-";
        try
        {
            key.gameObject.SetActive(false);
            SetRoomFillContribution(scene, true);
            Capture(camera, avatar.transform, prefix + "before.png", true);
            key.gameObject.SetActive(true);
            SetRoomFillContribution(scene, false);
            Capture(camera, avatar.transform, prefix + "after.png", true);
            Capture(camera, avatar.transform, prefix + "wide.png", false);
        }
        finally
        {
            key.gameObject.SetActive(active);
            foreach (var entry in masks) entry.light.cullingMask = entry.cullingMask;
        }
        Debug.Log("[NevaPortraitLighting] Captured identical-pose portrait comparison.");
    }

    private static void SetRoomFillContribution(Scene scene, bool includePortrait)
    {
        var layer = LayerMask.NameToLayer(LayerName);
        if (layer < 0) return;
        // Only remove the character from the existing fill lights. Their influence on
        // every wall/furniture renderer is identical; the portrait key replaces them on NEVA.
        var rig = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "[NeEEvA Realtime Fill - No Bake]");
        if (rig == null) return;
        foreach (var light in rig.GetComponentsInChildren<Light>(true))
        {
            Undo.RecordObject(light, "Set portrait fill contribution");
            light.cullingMask = includePortrait ? light.cullingMask | (1 << layer) : light.cullingMask & ~(1 << layer);
        }
    }

    private static int GetPortraitLayer()
    {
        var index = LayerMask.NameToLayer(LayerName);
        if (index >= 0) return index;
        var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = settings.FindProperty("layers");
        for (var i = 8; i < 31; i++)
        {
            if (!string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) continue;
            if (Resources.FindObjectsOfTypeAll<GameObject>().Any(go => go.scene.IsValid() && go.layer == i)) continue;
            layers.GetArrayElementAtIndex(i).stringValue = LayerName;
            settings.ApplyModifiedProperties();
            return i;
        }
        throw new InvalidOperationException("No unused user layer for portrait lighting.");
    }

    private static void Capture(Camera camera, Transform avatar, string filename, bool closeup)
    {
        const int width = 1200, height = 1200;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
        var oldTarget = camera.targetTexture;
        var oldActive = RenderTexture.active;
        var oldAspect = camera.aspect;
        var position = camera.transform.localPosition;
        var rotation = camera.transform.localRotation;
        try
        {
            if (closeup)
            {
                var aim = avatar.position + Vector3.up * 1.34f;
                camera.transform.position = aim + avatar.forward * 0.62f;
                camera.transform.LookAt(aim);
            }
            camera.targetTexture = target;
            camera.aspect = 1f;
            camera.Render();
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(Path.Combine(Output, filename), texture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = oldTarget;
            camera.aspect = oldAspect;
            camera.transform.localPosition = position;
            camera.transform.localRotation = rotation;
            RenderTexture.active = oldActive;
            UnityEngine.Object.DestroyImmediate(texture);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
