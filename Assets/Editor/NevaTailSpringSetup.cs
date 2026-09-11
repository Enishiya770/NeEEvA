using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Installs the independent tail simulation without enabling the full VRM runtime.</summary>
public static class NevaTailSpringSetup
{
    public const string DefaultScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string MenuPath = "NeEEvA/Tail/Configure Selected Avatar";
    private const string UndoLabel = "Configure natural tail";

    [MenuItem(MenuPath)]
    public static void ConfigureSelectedAvatar()
    {
        EnsureEditMode();
        var avatar = FindSelectedAvatar();
        if (avatar == null)
            throw new InvalidOperationException("Select a scene avatar with a FoxTail bone chain, or one of its child objects.");
        ConfigureAvatar(avatar, true);
        Debug.Log("[NevaTailSpringSetup] Natural tail configured on " + avatar.name + ". Save the scene to keep it.", avatar);
    }

    [MenuItem(MenuPath, true)]
    private static bool ValidateSelectedAvatar()
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode && FindSelectedAvatar() != null;
    }

    /// <summary>Configures the unique compatible avatar in a loaded scene; saving remains the caller's choice.</summary>
    public static NevaTailSpring ConfigureLoadedScene(Scene scene, bool registerUndo = false)
    {
        EnsureEditMode();
        if (!scene.IsValid() || !scene.isLoaded)
            throw new ArgumentException("The target scene must be loaded.", nameof(scene));

        Vrm10Instance selected = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var avatar in root.GetComponentsInChildren<Vrm10Instance>(true))
            {
                if (FindTailTransforms(avatar) == null) continue;
                if (selected != null)
                    throw new InvalidOperationException("The scene has multiple FoxTail avatars. Select one and use " + MenuPath + ".");
                selected = avatar;
            }
        }
        if (selected == null)
            throw new InvalidOperationException("No VRM avatar with a continuous FoxTail bone chain was found in " + scene.path + ".");
        return ConfigureAvatar(selected, registerUndo);
    }

    /// <summary>Unity -batchmode -executeMethod NevaTailSpringSetup.ConfigureSceneBatch [-tailScene Assets/...unity].</summary>
    public static void ConfigureSceneBatch()
    {
        // Keep an accidental menu/console call from closing an interactive editor.
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
            {
                // Do not replace or discard other loaded scenes, including their unsaved changes.
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            var tail = ConfigureLoadedScene(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Could not save the configured scene: " + scenePath);
            Debug.Log("[NevaTailSpringSetup] Saved natural tail on " + tail.name + " in " + scenePath +
                "; idle yaw=" + tail.idleSwayDegrees + ", lift=" + tail.idleLiftDegrees + ", period=" + tail.idleSwayPeriod);
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    private static NevaTailSpring ConfigureAvatar(Vrm10Instance avatar, bool registerUndo)
    {
        if (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
            throw new InvalidOperationException("Configure a scene instance, not the imported VRM asset.");
        var transforms = FindTailTransforms(avatar);
        if (transforms == null)
            throw new InvalidOperationException("The selected VRM does not have a continuous FoxTail bone chain.");
        if (avatar.GetComponents<NevaTailSpring>().Length > 1)
            throw new InvalidOperationException("The avatar already has multiple NevaTailSpring components.");

        // These serialized components are owned by the avatar/animation systems, not this installer.
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
            foreach (var bone in transforms) Undo.RecordObject(bone, UndoLabel);
        }

        try
        {
            var tail = avatar.GetComponent<NevaTailSpring>();
            if (tail == null)
                tail = registerUndo ? Undo.AddComponent<NevaTailSpring>(avatar.gameObject) : avatar.gameObject.AddComponent<NevaTailSpring>();
            if (registerUndo) Undo.RecordObject(tail, UndoLabel);
            tail.Configure(avatar);

            if (EditorJsonUtility.ToJson(avatar) != vrmBefore)
                throw new InvalidOperationException("Tail configuration unexpectedly changed VRM settings; the scene was not saved.");
            for (int i = 0; i < animators.Length; ++i)
                if (EditorJsonUtility.ToJson(animators[i]) != animatorBefore[i])
                    throw new InvalidOperationException("Tail configuration unexpectedly changed Animator settings; the scene was not saved.");

            // The natural edit-mode pose and serialized rest references must survive prefab reloads.
            foreach (var bone in transforms) RecordModification(bone);
            RecordModification(tail);
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            if (registerUndo) Undo.CollapseUndoOperations(undoGroup);
            return tail;
        }
        catch
        {
            if (registerUndo) Undo.RevertAllDownToGroup(undoGroup);
            throw;
        }
    }

    private static void RecordModification(UnityEngine.Object target)
    {
        EditorUtility.SetDirty(target);
        if (PrefabUtility.IsPartOfPrefabInstance(target))
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
    }

    private static Vrm10Instance FindSelectedAvatar()
    {
        var selected = Selection.activeGameObject;
        if (selected == null || EditorUtility.IsPersistent(selected) || !selected.scene.IsValid()) return null;
        var parentAvatar = selected.GetComponentInParent<Vrm10Instance>(true);
        if (parentAvatar != null && FindTailTransforms(parentAvatar) != null) return parentAvatar;
        Vrm10Instance result = null;
        foreach (var avatar in selected.GetComponentsInChildren<Vrm10Instance>(true))
        {
            if (FindTailTransforms(avatar) == null) continue;
            if (result != null) return null;
            result = avatar;
        }
        return result;
    }

    private static List<Transform> FindTailTransforms(Vrm10Instance avatar)
    {
        if (avatar == null) return null;
        if (avatar.SpringBone != null && avatar.SpringBone.Springs != null)
        {
            foreach (var spring in avatar.SpringBone.Springs)
            {
                if (spring == null || string.IsNullOrEmpty(spring.Name) ||
                    spring.Name.IndexOf("FoxTail", StringComparison.OrdinalIgnoreCase) < 0 || spring.Joints == null) continue;
                var chain = new List<Transform>();
                foreach (var joint in spring.Joints)
                {
                    if (joint == null || !joint.transform.IsChildOf(avatar.transform) ||
                        (chain.Count > 0 && joint.transform.parent != chain[chain.Count - 1]))
                    {
                        chain.Clear();
                        break;
                    }
                    chain.Add(joint.transform);
                }
                if (chain.Count >= 5)
                {
                    AppendTailChildren(chain);
                    return chain;
                }
            }
        }

        // Also allow an imported avatar whose original spring metadata was removed.
        foreach (var bone in avatar.GetComponentsInChildren<Transform>(true))
        {
            if (bone.name != "J_Opt_C_FoxTail1_01") continue;
            var chain = new List<Transform> { bone };
            AppendTailChildren(chain);
            if (chain.Count >= 5) return chain;
        }
        return null;
    }

    private static void AppendTailChildren(List<Transform> chain)
    {
        for (;;)
        {
            Transform next = null;
            foreach (Transform child in chain[chain.Count - 1])
            {
                if (child.name.IndexOf("FoxTail", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // A branched chain cannot be configured unambiguously.
                if (next != null) return;
                next = child;
            }
            if (next == null) return;
            chain.Add(next);
        }
    }

    private static string ReadSceneArgument()
    {
        var args = Environment.GetCommandLineArgs();
        string path = DefaultScenePath;
        for (int i = 0; i < args.Length; ++i)
        {
            if (!string.Equals(args[i], "-tailScene", StringComparison.OrdinalIgnoreCase)) continue;
            if (++i >= args.Length || args[i].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("-tailScene requires a project-relative scene asset path.");
            path = args[i];
        }
        path = path.Replace('\\', '/');
        if (!path.StartsWith("Assets/", StringComparison.Ordinal) ||
            !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOf("/../", StringComparison.Ordinal) >= 0 || path.IndexOf("/./", StringComparison.Ordinal) >= 0)
            throw new ArgumentException("-tailScene must point to a .unity scene inside Assets.");
        return path;
    }

    private static void EnsureEditMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Configure the tail outside Play mode.");
    }
}
