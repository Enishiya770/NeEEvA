using System;
using NeEEvA.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using Unity.XR.OpenXR.Features.PICOSupport;

public static class PlayerCameraSetup
{
    [MenuItem("NeEEvA/Player Camera/Configure PC VR and PICO")]
    public static void ConfigurePlatforms()
    {
        if (!AssetDatabase.IsValidFolder("Assets/XR")) AssetDatabase.CreateFolder("Assets", "XR");
        const string path = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey,
            out XRGeneralSettingsPerBuildTarget targets);
        if (targets == null) targets = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
        if (targets == null)
        {
            targets = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(targets, path);
        }
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, targets, true);
        ConfigureLoader(targets, BuildTargetGroup.Standalone);
        ConfigureLoader(targets, BuildTargetGroup.Android);

        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Standalone);
        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var pc = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        var pico = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (pc == null || pico == null)
            throw new InvalidOperationException("Install Unity Android Build Support before configuring PICO.");
        EnableFeature<KHRSimpleControllerProfile>(pc);
        EnableFeature<HTCViveControllerProfile>(pc);
        EnableFeature<ValveIndexControllerProfile>(pc);
        EnableFeature<OculusTouchControllerProfile>(pc);
        EnableFeature<PICOFeature>(pico);
        EnableFeature<OpenXRExtensions>(pico);
        EnableFeature<PICONeo3ControllerProfile>(pico);
        EnableFeature<PICO4ControllerProfile>(pico);
        EnableFeature<PICO4UltraControllerProfile>(pico);
        pc.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
        pico.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
        EditorUtility.SetDirty(pc);
        EditorUtility.SetDirty(pico);

        // Both retains the existing chat UI's legacy input while OpenXR uses Input System.
        var playerSettings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        playerSettings.FindProperty("activeInputHandler").intValue = 2;
        playerSettings.ApplyModifiedPropertiesWithoutUndo();
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        EditorUtility.SetDirty(targets);
        AssetDatabase.SaveAssets();
        Debug.Log("[Player Camera] PC OpenXR and PICO Android configured. Restart Unity after changing Active Input Handling.");
    }

    private static void ConfigureLoader(XRGeneralSettingsPerBuildTarget targets, BuildTargetGroup group)
    {
        if (!targets.HasSettingsForBuildTarget(group)) targets.CreateDefaultSettingsForBuildTarget(group);
        if (!targets.HasManagerSettingsForBuildTarget(group)) targets.CreateDefaultManagerSettingsForBuildTarget(group);
        var general = targets.SettingsForBuildTarget(group);
        var manager = targets.ManagerSettingsForBuildTarget(group);
        general.InitManagerOnStart = true;
        manager.automaticLoading = true;
        manager.automaticRunning = true;
        if (!XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group))
            throw new InvalidOperationException("Could not assign the OpenXR loader for " + group);
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(manager);
    }

    private static void EnableFeature<T>(OpenXRSettings settings) where T : OpenXRFeature
    {
        var feature = settings.GetFeature<T>();
        if (feature == null) throw new InvalidOperationException("Missing OpenXR feature " + typeof(T).Name);
        feature.enabled = true;
        EditorUtility.SetDirty(feature);
    }

    [MenuItem("NeEEvA/Player Camera/Enable on Selected Camera")]
    public static void EnableSelectedCamera()
    {
        var camera = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<Camera>() : Camera.main;
        if (camera == null) throw new InvalidOperationException("Select the player Camera in the Hierarchy first.");
        Undo.RecordObject(camera, "Configure player camera");
        Undo.RecordObject(camera.gameObject, "Tag player camera");
        camera.gameObject.tag = "MainCamera";
        camera.orthographic = false;
        camera.nearClipPlane = 0.03f;
        camera.stereoTargetEye = StereoTargetEyeMask.Both;
        if (camera.GetComponent<PlayerCameraController>() == null)
            Undo.AddComponent<PlayerCameraController>(camera.gameObject);
        EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
    }
}
