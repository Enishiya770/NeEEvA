using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.OpenXR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Unity.XR.OpenXR.Features.PICOSupport;

/// <summary>Run only in an isolated project, with -executeMethod PlayerCameraRegression.RunBatch (no -quit).</summary>
[InitializeOnLoad]
public static class PlayerCameraRegression
{
    private const string Key = "NeEEvA.CameraRegression";
    private static readonly List<string> checks = new List<string>();
    private static string failure;

    static PlayerCameraRegression()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Run;
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Key, false);
                PlayerCameraSetup.ConfigurePlatforms();
                EditorApplication.Exit(SessionState.GetBool(Key + ".passed", false) ? 0 : 1);
            }
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || !Application.dataPath.Replace('\\', '/').Contains("/Logs/PlayerCameraValidation/"))
            throw new InvalidOperationException("Run this regression in Logs/PlayerCameraValidation only.");
        PlayerCameraSetup.ConfigurePlatforms();
        foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
        {
            var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            if (general == null || general.Manager.activeLoaders.Count != 1 || !general.InitManagerOnStart)
                throw new InvalidOperationException("OpenXR startup is not configured for " + group);
            // The hardware-free regression drives Input System events itself.
            general.InitManagerOnStart = false;
            EditorUtility.SetDirty(general);
        }
        if (!OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android).GetFeature<PICOFeature>().enabled)
            throw new InvalidOperationException("PICO Support is disabled.");
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/CameraRegression.unity");
        SessionState.SetBool(Key, true);
        EditorApplication.isPlaying = true;
    }

    public static void ValidateConfigurationBatch()
    {
        PlayerCameraSetup.ConfigurePlatforms();
        var messages = new List<string>();
        bool passed = true;
        foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
        {
            var issues = new List<OpenXRFeature.ValidationRule>();
            OpenXRProjectValidation.GetCurrentValidationIssues(issues, group);
            foreach (var issue in issues)
            {
                messages.Add(group + ": " + (issue.error ? "ERROR " : "WARNING ") + issue.message);
                if (issue.error) passed = false;
            }
        }
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/player-camera-platform-validation.json", JsonUtility.ToJson(new Report
        {
            passed = passed, checks = messages.ToArray(),
            scope = "Unity OpenXR built-in and enabled PICO feature validation; active compilation target: " + EditorUserBuildSettings.activeBuildTarget
        }, true));
        if (!passed) throw new InvalidOperationException(string.Join("\n", messages));
    }

    private static void Run()
    {
        GameObject parent = null, cameraObject = null;
        XRHMD hmd = null;
        try
        {
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            parent = new GameObject("Rotated scaled room");
            parent.transform.SetPositionAndRotation(new Vector3(-3f, -0.72f, -4f), Quaternion.Euler(0f, -90f, 0f));
            parent.transform.localScale = Vector3.one * 2f;
            cameraObject = new GameObject("Player Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(parent.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 1.22f, -1.54f);
            Pose spawn = new Pose(cameraObject.transform.position, cameraObject.transform.rotation);
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            var controller = cameraObject.AddComponent<PlayerCameraController>();
            controller.enabled = false; // Session transitions are explicitly simulated below.
            Check(Vector3.Distance(camera.transform.position, spawn.position) < 0.0001f, "Original world eye position retained");
            Check(!camera.orthographic && camera.nearClipPlane <= 0.03f, "Perspective and VR near clip configured");
            Check(Camera.main == camera, "MainCamera identity retained");
            Check(camera.transform.parent.lossyScale == Vector3.one, "Tracking origin has unit world scale");
            Check(camera.GetComponents<TrackedPoseDriver>().Length == 1, "Exactly one head pose driver");

            Call(controller, "ApplyDesktopMotion", Vector2.zero, Vector3.forward, false, 0.5f);
            Check(Vector3.Distance(camera.transform.position, spawn.position + spawn.rotation * Vector3.forward) < 0.0001f, "W follows camera heading at configured metres per second");
            controller.ResetView();
            Call(controller, "ApplyDesktopMotion", Vector2.zero, new Vector3(1f, 0f, 1f), false, 0.5f);
            Check(Mathf.Abs(Vector3.Distance(camera.transform.position, spawn.position) - 1f) < 0.0001f, "Diagonal flight is not faster");
            controller.ResetView();
            Call(controller, "ApplyDesktopMotion", Vector2.zero, Vector3.up, true, 0.5f);
            Check(Vector3.Distance(camera.transform.position, spawn.position + Vector3.up * 3f) < 0.0001f, "Shift boosts world-up flight");
            controller.ResetView();
            Call(controller, "ApplyDesktopMotion", new Vector2(0f, -10000f), Vector3.zero, false, 0f);
            Check(Mathf.Abs(Mathf.DeltaAngle(0f, camera.transform.eulerAngles.x) - 89f) < 0.001f, "Pitch cannot flip");
            controller.ResetView();
            for (int i = 0; i < 60; ++i) Call(controller, "ApplyDesktopMotion", Vector2.zero, Vector3.forward, false, 1f / 60f);
            Check(Vector3.Distance(camera.transform.position, spawn.position + spawn.rotation * Vector3.forward * 2f) < 0.001f, "Flight speed independent of frame subdivision");
            controller.ResetView();
            Call(controller, "UpdateDesktop");
            Check(!controller.IsLooking && Vector3.Distance(camera.transform.position, spawn.position) < 0.0001f, "No right mouse capture means no desktop movement");

            hmd = InputSystem.AddDevice<XRHMD>();
            Call(controller, "SetVRActive", true);
            Check(controller.IsVRActive && !camera.GetComponent<TrackedPoseDriver>().enabled, "VR waits for a valid six-degree pose");
            Vector3 firstPosition = new Vector3(0.4f, 1.7f, -0.2f);
            Quaternion firstRotation = Quaternion.Euler(0f, 35f, 0f);
            SendHead(hmd, firstPosition, firstRotation, 3);
            Call(controller, "AlignHeadToDesktopPose");
            UpdateGameInput();
            Check(camera.GetComponent<TrackedPoseDriver>().enabled, "Valid pose enables Unity TrackedPoseDriver");
            Check(Vector3.Distance(camera.transform.position, spawn.position) < 0.001f, "Headset eye aligned without adding a second eye height");
            Check(Quaternion.Angle(camera.transform.rotation, spawn.rotation) < 0.001f, "Initial headset heading aligned to scene");
            Quaternion originRotation = camera.transform.parent.rotation;
            Vector3 step = new Vector3(0.2f, -0.35f, 0.5f);
            Quaternion tilted = Quaternion.Euler(20f, 55f, 12f);
            SendHead(hmd, firstPosition + step, tilted, 3);
            Check(Vector3.Distance(camera.transform.position, spawn.position + originRotation * step) < 0.001f, "Physical movement follows 1:1 through a rotated room");
            Check(Quaternion.Angle(camera.transform.rotation, originRotation * tilted) < 0.001f, "Head pitch yaw and roll all reach the camera");
            Pose tracked = new Pose(camera.transform.position, camera.transform.rotation);
            SendHead(hmd, Vector3.one * 100f, Quaternion.identity, 0);
            Check(Vector3.Distance(camera.transform.position, tracked.position) < 0.001f && Quaternion.Angle(camera.transform.rotation, tracked.rotation) < 0.001f, "Invalid tracking does not teleport the camera");
            Check(controller.IsVRActive, "Tracking loss does not return to desktop control");
            SendHead(hmd, firstPosition, firstRotation, 3);
            Check(Vector3.Distance(camera.transform.position, spawn.position) < 0.001f, "Tracking resumes without a new origin offset");
            Call(controller, "SetVRActive", false);
            Check(!controller.IsVRActive && !camera.GetComponent<TrackedPoseDriver>().enabled, "Ending XR disables head tracking");
            Check(Vector3.Distance(camera.transform.position, spawn.position) < 0.001f && Quaternion.Angle(camera.transform.rotation, spawn.rotation) < 0.001f, "Desktop view restored after XR session");
            SendHead(hmd, Vector3.one * 20f, Quaternion.identity, 3);
            Check(Vector3.Distance(camera.transform.position, spawn.position) < 0.001f, "Inactive XR input cannot overwrite desktop camera");
        }
        catch (Exception ex) { failure = ex.ToString(); Debug.LogException(ex); }
        finally
        {
            if (hmd != null) InputSystem.RemoveDevice(hmd);
            if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
            if (parent != null) UnityEngine.Object.Destroy(parent);
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/player-camera-regression.json", JsonUtility.ToJson(new Report
            {
                passed = failure == null, error = failure, checks = checks.ToArray(),
                scope = "Unity Play Mode with production movement code and Unity TrackedPoseDriver fed simulated Input System HMD poses. XR session transitions simulated; no physical headset or pointer-lock UI test."
            }, true));
            SessionState.SetBool(Key + ".passed", failure == null);
            EditorApplication.delayCall += () => EditorApplication.isPlaying = false;
        }
    }

    private static void SendHead(XRHMD hmd, Vector3 position, Quaternion rotation, int tracking)
    {
        InputSystem.QueueDeltaStateEvent(hmd.trackingState, tracking);
        InputSystem.QueueDeltaStateEvent(hmd.centerEyePosition, position);
        InputSystem.QueueDeltaStateEvent(hmd.centerEyeRotation, rotation);
        UpdateGameInput();
    }
    private static void UpdateGameInput()
    {
        // A batch Editor has no focused Game view. Explicitly process the player buffer.
        typeof(InputSystem).GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
    }
    private static void Call(object target, string method, params object[] args)
    {
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    [Serializable] private sealed class Report { public bool passed; public string error, scope; public string[] checks; }
}
