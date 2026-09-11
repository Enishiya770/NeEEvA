using System;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Connects an avatar's independent eye tracking to its scene's player camera.</summary>
public static class NevaEyeTrackingSetup
{
    public const string DefaultScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string MenuPath = "NeEEvA/Eyes/Configure Selected Avatar";
    private const string UndoLabel = "Configure eye tracking";

    [MenuItem(MenuPath)]
    public static void ConfigureSelectedAvatar()
    {
        EnsureEditMode();
        var avatar = FindSelectedAvatar();
        if (avatar == null)
            throw new InvalidOperationException("Select a scene VRM avatar with head and eye bones, or one of its child objects.");
        var eyes = ConfigureAvatar(avatar, true);
        Debug.Log("[NevaEyeTrackingSetup] Eye tracking configured on " + avatar.name +
            "; target camera=" + eyes.gazeTarget.name + ". Save the scene to keep it.", eyes);
    }

    [MenuItem(MenuPath, true)]
    private static bool ValidateSelectedAvatar()
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode && FindSelectedAvatar() != null;
    }

    /// <summary>Configures one compatible avatar in a loaded scene; the caller decides when to save.</summary>
    public static NevaEyeTracking ConfigureLoadedScene(Scene scene, bool registerUndo = false)
    {
        EnsureEditMode();
        if (!scene.IsValid() || !scene.isLoaded)
            throw new ArgumentException("The target scene must be loaded.", nameof(scene));

        Vrm10Instance selected = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var avatar in root.GetComponentsInChildren<Vrm10Instance>(true))
            {
                if (!HasEyeBones(avatar)) continue;
                if (selected != null)
                    throw new InvalidOperationException("The scene has multiple VRM avatars with eye bones. Select one and use " + MenuPath + ".");
                selected = avatar;
            }
        }
        if (selected == null)
            throw new InvalidOperationException("No VRM avatar with head and both eye bones was found in " + scene.path + ".");
        return ConfigureAvatar(selected, registerUndo);
    }

    /// <summary>Unity -batchmode -executeMethod NevaEyeTrackingSetup.ConfigureSceneBatch [-eyeScene Assets/...unity].</summary>
    public static void ConfigureSceneBatch()
    {
        // Never close an interactive editor through an accidental console call.
        if (!Application.isBatchMode)
            throw new InvalidOperationException("ConfigureSceneBatch is only available in Unity batch mode.");
        try
        {
            EnsureEditMode();
            string scenePath = ReadSceneArgument();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                throw new InvalidOperationException("The requested scene asset does not exist: " + scenePath);
            var scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            var eyes = ConfigureLoadedScene(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Could not save the configured scene: " + scenePath);
            Debug.Log("[NevaEyeTrackingSetup] Saved eye tracking on " + eyes.name + " in " + scenePath +
                "; target camera=" + eyes.gazeTarget.name);
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    private static NevaEyeTracking ConfigureAvatar(Vrm10Instance avatar, bool registerUndo)
    {
        if (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
            throw new InvalidOperationException("Configure a scene instance, not the imported VRM asset.");
        if (!HasEyeBones(avatar))
            throw new InvalidOperationException("The selected VRM requires a head and both humanoid eye bones.");
        if (avatar.GetComponents<NevaEyeTracking>().Length > 1)
            throw new InvalidOperationException("The avatar already has multiple NevaEyeTracking components.");

        // Resolve ambiguity before making any scene modifications. Camera.main may belong to another loaded scene.
        var camera = FindSceneCamera(avatar.gameObject.scene);
        string vrmBefore = EditorJsonUtility.ToJson(avatar);
        var animators = avatar.GetComponentsInChildren<Animator>(true);
        var animatorBefore = new string[animators.Length];
        for (int i = 0; i < animators.Length; ++i)
            animatorBefore[i] = EditorJsonUtility.ToJson(animators[i]);

        int undoGroup = -1;
        if (registerUndo)
        {
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabel);
        }

        try
        {
            var eyes = avatar.GetComponent<NevaEyeTracking>();
            if (eyes == null)
                eyes = registerUndo ? Undo.AddComponent<NevaEyeTracking>(avatar.gameObject) : avatar.gameObject.AddComponent<NevaEyeTracking>();
            if (registerUndo) Undo.RecordObject(eyes, UndoLabel);
            eyes.Configure(avatar, camera.transform);

            if (EditorJsonUtility.ToJson(avatar) != vrmBefore)
                throw new InvalidOperationException("Eye tracking configuration unexpectedly changed VRM settings; the scene was not saved.");
            for (int i = 0; i < animators.Length; ++i)
                if (EditorJsonUtility.ToJson(animators[i]) != animatorBefore[i])
                    throw new InvalidOperationException("Eye tracking configuration unexpectedly changed Animator settings; the scene was not saved.");
            if (eyes.gazeTarget != camera.transform)
                throw new InvalidOperationException("Eye tracking did not retain the selected player camera reference; the scene was not saved.");

            EditorUtility.SetDirty(eyes);
            if (PrefabUtility.IsPartOfPrefabInstance(eyes))
                PrefabUtility.RecordPrefabInstancePropertyModifications(eyes);
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            if (registerUndo) Undo.CollapseUndoOperations(undoGroup);
            return eyes;
        }
        catch
        {
            if (registerUndo) Undo.RevertAllDownToGroup(undoGroup);
            throw;
        }
    }

    private static Camera FindSceneCamera(Scene scene)
    {
        Camera result = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
            {
                if (!camera.isActiveAndEnabled || !camera.CompareTag("MainCamera")) continue;
                if (result != null)
                    throw new InvalidOperationException("The avatar's scene has multiple active MainCamera cameras; keep one player camera active before configuring eye tracking.");
                result = camera;
            }
        }
        if (result == null)
            throw new InvalidOperationException("The avatar's scene needs an active camera tagged MainCamera before configuring eye tracking.");
        return result;
    }

    private static Vrm10Instance FindSelectedAvatar()
    {
        var selected = Selection.activeGameObject;
        if (selected == null || EditorUtility.IsPersistent(selected) || !selected.scene.IsValid()) return null;
        var parentAvatar = selected.GetComponentInParent<Vrm10Instance>(true);
        if (HasEyeBones(parentAvatar)) return parentAvatar;
        Vrm10Instance result = null;
        foreach (var avatar in selected.GetComponentsInChildren<Vrm10Instance>(true))
        {
            if (!HasEyeBones(avatar)) continue;
            if (result != null) return null;
            result = avatar;
        }
        return result;
    }

    private static bool HasEyeBones(Vrm10Instance avatar)
    {
        if (avatar == null || avatar.Vrm == null || avatar.Humanoid == null) return false;
        var humanoid = avatar.Humanoid;
        var head = humanoid.GetBoneTransform(HumanBodyBones.Head);
        var left = humanoid.GetBoneTransform(HumanBodyBones.LeftEye);
        var right = humanoid.GetBoneTransform(HumanBodyBones.RightEye);
        return head != null && left != null && right != null && left != right &&
            head.IsChildOf(avatar.transform) && left.IsChildOf(head) && right.IsChildOf(head);
    }

    private static string ReadSceneArgument()
    {
        var args = Environment.GetCommandLineArgs();
        string path = DefaultScenePath;
        for (int i = 0; i < args.Length; ++i)
        {
            if (!string.Equals(args[i], "-eyeScene", StringComparison.OrdinalIgnoreCase)) continue;
            if (++i >= args.Length || args[i].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("-eyeScene requires a project-relative scene asset path.");
            path = args[i];
        }
        path = path.Replace('\\', '/');
        if (!path.StartsWith("Assets/", StringComparison.Ordinal) ||
            !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOf("/../", StringComparison.Ordinal) >= 0 || path.IndexOf("/./", StringComparison.Ordinal) >= 0)
            throw new ArgumentException("-eyeScene must point to a .unity scene inside Assets.");
        return path;
    }

    private static void EnsureEditMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Configure eye tracking outside Play mode.");
    }
}
