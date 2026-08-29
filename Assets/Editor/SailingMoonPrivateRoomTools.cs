using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class SailingMoonPrivateRoomTools
{
    private const string EnvironmentScene =
        "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";
    private const string IntegratedScene =
        "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string ChatScene = "Assets/AIChatTookit/Scene/chatSample.unity";

    // Presentation point in the sunken living room: outside the coffee table,
    // with the camera on the TV side and the sectional sofa as the backdrop.
    private static readonly Vector3 ChatRigPosition =
        new Vector3(-2.9f, -0.72f, -4.6f);
    private static readonly Quaternion ChatRigRotation =
        Quaternion.Euler(0f, -90f, 0f);

    [MenuItem("NeEEvA/Private Rooms/Build SailingMoon Day Chat Scene")]
    public static void BuildIntegratedScene()
    {
        RequireScene(EnvironmentScene);
        RequireScene(ChatScene);

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(IntegratedScene) != null &&
            !AssetDatabase.DeleteAsset(IntegratedScene))
        {
            throw new InvalidOperationException("Could not replace " + IntegratedScene);
        }

        if (!AssetDatabase.CopyAsset(EnvironmentScene, IntegratedScene))
        {
            throw new InvalidOperationException(
                "Could not copy the private environment scene to " + IntegratedScene);
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var destination = EditorSceneManager.OpenScene(IntegratedScene, OpenSceneMode.Single);
        var chat = EditorSceneManager.OpenScene(ChatScene, OpenSceneMode.Additive);

        var chatRig = new GameObject("[NeEEvA Chat Rig]");
        SceneManager.MoveGameObjectToScene(chatRig, destination);

        foreach (var root in chat.GetRootGameObjects())
        {
            // SailingMoon owns the baked/mixed environment lighting.
            if (string.Equals(root.name, "Directional Light", StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(root);
                continue;
            }

            SceneManager.MoveGameObjectToScene(root, destination);
            root.transform.SetParent(chatRig.transform, true);
        }

        chatRig.transform.SetPositionAndRotation(
            ChatRigPosition,
            ChatRigRotation);

        RepairPrivateChatUi(destination);

        EditorSceneManager.CloseScene(chat, true);
        EditorSceneManager.MarkSceneDirty(destination);
        if (!EditorSceneManager.SaveScene(destination, IntegratedScene, false))
        {
            throw new InvalidOperationException("Could not save " + IntegratedScene);
        }

        Validate(destination);
        AssetDatabase.SaveAssets();
        Debug.Log("[SailingMoon] Integrated private chat scene created: " + IntegratedScene);
    }

    public static void BuildIntegratedSceneBatch()
    {
        BuildIntegratedScene();
    }

    public static void CapturePreviewBatch()
    {
        RequireScene(IntegratedScene);
        var scene = EditorSceneManager.OpenScene(IntegratedScene, OpenSceneMode.Single);
        var camera = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .FirstOrDefault(candidate => candidate.gameObject.name == "Camera") ??
            scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                .FirstOrDefault();
        if (camera == null)
        {
            throw new InvalidOperationException("No camera is available for the private room preview.");
        }

        const int width = 1280;
        const int height = 720;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply(false, false);
            var output = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "LocalPrivate", "SailingMoonPreview.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllBytes(output, image.EncodeToPNG());
            Debug.Log("[SailingMoon] Preview captured: " + output);
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    public static void ReportLayoutBatch()
    {
        RequireScene(EnvironmentScene);
        var scene = EditorSceneManager.OpenScene(EnvironmentScene, OpenSceneMode.Single);
        var tokens = new[]
        {
            "living", "sofa", "bed", "carpet", "table", "desk", "chair", "floor"
        };
        var renderers = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Renderer>(true))
            .Where(renderer => renderer != null)
            .ToArray();
        foreach (var renderer in renderers.Where(renderer =>
            tokens.Any(token => renderer.gameObject.name.IndexOf(
                token, StringComparison.OrdinalIgnoreCase) >= 0)))
        {
            Debug.Log(
                $"[SailingMoon Layout] {renderer.gameObject.name}: " +
                $"position={renderer.transform.position}, center={renderer.bounds.center}, " +
                $"size={renderer.bounds.size}");
        }

        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1))
            {
                bounds.Encapsulate(renderer.bounds);
            }
            Debug.Log(
                $"[SailingMoon Layout] Combined: center={bounds.center}, size={bounds.size}");
        }
    }

    private static void RequireScene(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
        {
            throw new System.IO.FileNotFoundException("Required scene not found", path);
        }
    }

    private static void RepairPrivateChatUi(Scene scene)
    {
        var builtinSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>(
            "UI/Skin/UISprite.psd");
        if (builtinSprite == null)
        {
            throw new InvalidOperationException("Unity built-in UI sprite was not found.");
        }

        var settingImages = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Image>(true))
            .Where(image => image.gameObject.name == "Setting" && image.sprite == null)
            .ToArray();
        foreach (var image in settingImages)
        {
            image.sprite = builtinSprite;
            EditorUtility.SetDirty(image);
        }
    }

    private static void Validate(Scene scene)
    {
        var gameObjects = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Select(transform => transform.gameObject)
            .Distinct()
            .ToArray();
        var missingScripts = gameObjects.Sum(
            GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);
        var renderers = gameObjects.Sum(go => go.GetComponents<Renderer>().Length);
        var colliders = gameObjects.Sum(go => go.GetComponents<Collider>().Length);
        var lights = gameObjects.Sum(go => go.GetComponents<Light>().Length);
        var nullMaterialSlots = gameObjects
            .SelectMany(go => go.GetComponents<Renderer>())
            .Sum(renderer => renderer.sharedMaterials.Count(material => material == null));

        if (missingScripts != 0)
        {
            throw new InvalidOperationException(
                $"Integrated scene still contains {missingScripts} missing scripts.");
        }

        if (!gameObjects.Any(go => go.name == "NeEEvA"))
        {
            throw new InvalidOperationException("NeEEvA avatar was not found after merge.");
        }

        if (!gameObjects.Any(go => go.GetComponent<Camera>() != null))
        {
            throw new InvalidOperationException("Chat camera was not found after merge.");
        }

        if (nullMaterialSlots != 0)
        {
            throw new InvalidOperationException(
                $"Integrated scene contains {nullMaterialSlots} null material slots.");
        }

        Debug.Log(
            $"[SailingMoon] Validation: objects={gameObjects.Length}, " +
            $"renderers={renderers}, colliders={colliders}, lights={lights}, " +
            $"missingScripts={missingScripts}, nullMaterials={nullMaterialSlots}, " +
            $"lightmaps={LightmapSettings.lightmaps.Length}");
    }
}
