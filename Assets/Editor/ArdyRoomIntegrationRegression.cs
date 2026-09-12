using System;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;

/// <summary>Isolated actual player-loop, rendered VRM history and real ARDY HTTP integration.</summary>
[InitializeOnLoad]
public static class ArdyRoomIntegrationRegression
{
    private const string Key = "NeEEvA.ARDY.RoomIntegration.";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, avatarPath, avatarSha256, controllerSha256, playerSha256, windowSha256, regressionSha256, arrivalTimingSha256;
        public string roomScene, roomSceneSha256, environmentRoot;
        public string excludedVisualRoots;
        public int geometrySourceCount, geometryProxyCount;
        public int thinCoverCount;
        public string[] thinCoverDiagnostics;
        public List<CarpetGeometrySample> carpetGeometrySamples = new List<CarpetGeometrySample>();
        public string scope = "Isolated flat floor; real imported VRM, actual Animator and configured VRM LateUpdate history, one real HTTP locomotion generation and guarded full playback. No manual VRM.Process, SampleAt or synthetic initial history. See runtimeMode for direct versus ControlRig setup.";
        public string limitations = "Not a real-room geometry test, subjective naturalness score, finger control, navigation around moving users or delayed-network race proof. Cancellation check is immediate client cancellation; server cancellation has separate tests.";
        public int checks, observedPlaybackFrames, historyFrames, outputFrames;
        public bool realResponseReceived, completedNaturally, bodyOwnersRestored, cancellationPreservedRoot, guardBlockedBeforeMovement, externalOwnerPreserved;
        public float actualEndpointError, maximumRootTravel, historyRootHeight, initialYaw, avatarScale;
        public float maximumRenderedSourceRotationRoundtripDegrees;
        public string runtimeMode;
        public bool importedUseControlRig, forceControlRig, controlRigInitializedAtIdentity;
        public bool disabledVrm, boundViaWindow, disabledVrmPreserved, runtimeStayedUninitialized;
        public bool inactiveAvatarBindRejected, inactiveAvatarStopped, runtimeStateChangeRejected, controllerModeChangeRejected;
        public List<BodyPoseSample> bodyPoseSamples = new List<BodyPoseSample>();
        public string lastControllerStatus;
        public string arrivalObservation = "PostLateUpdate observations of actual Humanoid bones, with an independent same-skeleton Animator running the actual idle controller; no manual Animator.Update, VRM.Process or SampleAt. Baseline state is synchronized once when the return starts, then advances through Unity's player loop.";
        public List<ArrivalFrame> arrivalFrames = new List<ArrivalFrame>();
        public int observedReturnFrames, observedRestoredFrames;
        public float returnDuration, maximumReturnRootDrift, firstReturnRotationStepDegrees, firstReturnPositionStepMeters;
        public float maximumReturnRotationStepDegrees, maximumReturnPositionStepMeters, firstRestoredRotationStepDegrees, firstRestoredPositionStepMeters;
        public float maximumRestoredBaselineRotationErrorDegrees, maximumRestoredBaselinePositionErrorMeters;
        public float animatedBaselineRotationChangeDegrees, animatedBaselinePositionChangeMeters, animatedBaselineNormalizedTimeAdvance;
        public int terminalHoldStartFrame, playbackEndFrame;
        public float rawClipLastSampleSeconds, plannedArrivalSeconds, selectedPlaybackEndSeconds, observedReturnClipSeconds;
        public float plannedArrivalToReturnSeconds, skippedTailSeconds;
        public bool rawResponseClipPreserved, playbackEndRespected;
        public bool arrivalTransitionContinuous, arrivalMatchesDynamicAnimator, returnCancellationPreservedRoot, returnReplacementPreserved;
        public bool externalReturnPreserved, externalRootDuringReturnPreserved, externalAnimatorDisablePreserved, externalPoseDuringReturnPreserved;
        public string lifecycleReturnFixture = "Two-frame held copy of the real generated endpoint, driven by actual LateUpdate; tests ownership and interruption only, not generated motion quality.";
    }
    [Serializable] private sealed class ArrivalBone
    {
        public string bone;
        public Vector3 positionInRoot;
        public Quaternion rotationInRoot;
    }
    [Serializable] private sealed class ArrivalFrame
    {
        public int frame;
        public float realtime, deltaTime, clipSeconds, animatorNormalizedTime;
        public string stage;
        public bool returning, returned, animatorEnabled, hasPoseOwnership;
        public long revision;
        public Vector3 rootPosition;
        public Quaternion rootRotation;
        public List<ArrivalBone> actual = new List<ArrivalBone>(), independentIdle = new List<ArrivalBone>();
        public float adjacentRotationStepDegrees, adjacentPositionStepMeters, baselineRotationErrorDegrees, baselinePositionErrorMeters;
    }
    [Serializable] private sealed class BodyTransformSample
    {
        public string name, parent;
        public Vector3 worldPosition, localPosition, lossyScale, localScale;
        public Quaternion worldRotation, localRotation;
    }
    [Serializable] private sealed class BodyPoseSample
    {
        public string stage;
        public int frame;
        public float realtime, expectedSupportY, actualHipsAboveSupport;
        public bool animatorEnabled, vrmEnabled, runtimeInitialized, animatorTargetsActualHumanoid, animatorTargetsControlRig;
        public string animatorAvatar;
        public BodyTransformSample avatarRoot, actualHips, animatorHips, controlRigHips, controlRigRoot;
    }
    [Serializable] private sealed class CarpetGeometrySample
    {
        public string stage, path, sharedMeshName, sharedMeshAssetPath;
        public int frame, rendererInstanceId, meshInstanceId, vertexCount, subMeshCount;
        public float realtime;
        public bool isPlaying, enabled, activeInHierarchy, isPartOfStaticBatch, isReadable;
        public Vector3 position, localPosition, localScale, lossyScale;
        public Quaternion rotation;
        public Matrix4x4 localToWorld;
        public Bounds rendererBounds, sharedMeshBounds;
        public string[] ancestorAnimators;
    }
    [Serializable] private sealed class CarpetGeometrySamples
    {
        public List<CarpetGeometrySample> values = new List<CarpetGeometrySample>();
    }
    private static Report report;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyLocomotionAvatarReference reference;
    private static ArdyRoomNavigation navigation;
    private static ArdyRoomLocomotionController controller;
    private static ArdyRoomLocomotionWindow window;
    private static ArdyLocomotionClip clip;
    private static Vector3 startPosition, target, blockedPosition;
    private static int phase, lastFrame;
    private static float phaseTime;
    private static bool runtimeInitialized, useControlRig, disabledVrm;
    private static FieldInfo runtimeStorage;
    private static readonly HumanBodyBones[] ArrivalBones = {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
    };
    private static Animator idleBaseline;
    private static Dictionary<Transform, Transform> idleBaselineTransforms;
    private static ArrivalFrame precedingArrivalFrame, firstBaselineFrame;
    private static Exception playerLoopObservationError;
    private static float returnStartedAt;
    private static long arrivalRevision, fixtureRevision;
    private static Quaternion returnRootRotation, blockedRotation;
    private static Quaternion externalHandRotation;
    private static Vector3 returnRootPosition;
    private static bool baselineStarted, arrivalObserved, restoredObserved;
    private static ArdyLocomotionClip endpointFixture, replacementFixture;

    static ArdyRoomIntegrationRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try { Initialize(); }
                catch (Exception error) { Finish(error); }
            }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) FinalizeBatch();
        };
        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying)
                FinalizeBatch();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use only the isolated unity-naturalness-validation project in batchmode, without -quit.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("PlayMode already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a modified validation scene.");
            string asset = SelectAvatar();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            var imported = prefab.GetComponent<Vrm10Instance>();
            var importedReference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, asset);
            SessionState.SetString(Key + "Avatar", asset);
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-room-integration")));
            SessionState.SetString(Key + "Endpoint", Argument("-ardyRoomEndpoint", "http://127.0.0.1:8093"));
            string roomScene = Argument("-ardyRoomScene", "");
            SessionState.SetString(Key + "Scene", roomScene);
            SessionState.SetString(Key + "Environment", Argument("-ardyRoomEnvironmentName", "===オブジェクト==="));
            SessionState.SetString(Key + "Start", Argument("-ardyRoomStart", "0,0,0"));
            SessionState.SetString(Key + "Target", Argument("-ardyRoomTarget", ""));
            SessionState.SetFloat(Key + "InitialYaw", float.Parse(Argument("-ardyRoomInitialYaw", "37"), CultureInfo.InvariantCulture));
            float avatarScale = float.Parse(Argument("-ardyRoomAvatarScale", ".9"), CultureInfo.InvariantCulture);
            if (float.IsNaN(avatarScale) || float.IsInfinity(avatarScale) || avatarScale <= 0)
                throw new ArgumentException("Avatar scale must be finite and positive.");
            SessionState.SetFloat(Key + "AvatarScale", avatarScale);
            SessionState.SetBool(Key + "StartIsRoot", bool.Parse(Argument("-ardyRoomStartIsRoot", "false")));
            SessionState.SetString(Key + "VisualExclusions", Argument("-ardyRoomExcludedVisualRoots", ""));
            SessionState.SetBool(Key + "ForceControlRig", bool.Parse(Argument("-ardyRoomForceControlRig", "false")));
            SessionState.SetBool(Key + "DisabledVrm", bool.Parse(Argument("-ardyRoomDisabledVrm", "false")));
            if (SessionState.GetBool(Key + "DisabledVrm", false) && SessionState.GetBool(Key + "ForceControlRig", false))
                throw new ArgumentException("Disabled VRM must use actual Humanoid bones without constructing a ControlRig.");
            if (string.IsNullOrEmpty(roomScene)) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            else
            {
                if (roomScene.IndexOf("_Chat", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ArgumentException("Use the geometry-only room scene, never the private chat scene.");
                if (string.IsNullOrEmpty(SessionState.GetString(Key + "Target", "")))
                    throw new ArgumentException("An explicitly audited -ardyRoomTarget is required for a real room.");
                EditorSceneManager.OpenScene(roomScene, OpenSceneMode.Single);
                var environment = GameObject.Find(SessionState.GetString(Key + "Environment", ""));
                if (environment == null) throw new InvalidOperationException("Authored room environment was not found before entering Play.");
                ArdyRoomGeometryPreparation.Prepare(environment.transform, false);
            }
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            root.name = "ARDY-Room-Integration-Avatar";
            root.transform.localScale = Vector3.one * avatarScale;
            Vector3 startGround = ParseVector(SessionState.GetString(Key + "Start", ""));
            Vector3 rootPosition = SessionState.GetBool(Key + "StartIsRoot", false) ? startGround : startGround - Vector3.up * importedReference.groundY * avatarScale;
            root.transform.SetPositionAndRotation(rootPosition,
                Quaternion.Euler(0, SessionState.GetFloat(Key + "InitialYaw", 37), 0));
            var vrm = root.GetComponent<Vrm10Instance>();
            vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
            foreach (var owner in root.GetComponentsInChildren<Animator>(true))
            {
                owner.enabled = false; owner.runtimeAnimatorController = null; owner.applyRootMotion = false;
                owner.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            if (string.IsNullOrEmpty(roomScene))
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.name = "ARDY-Room-Integration-Environment";
                floor.transform.position = new Vector3(0, -.1f, 0);
                floor.transform.localScale = new Vector3(12, .2f, 12);
                ArdyRoomGeometryPreparation.Prepare(floor.transform, false);
            }
            SessionState.SetString(Key + "EditGeometry", JsonUtility.ToJson(new CarpetGeometrySamples { values = CaptureCarpetGeometry("edit-before-play") }));
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 120);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        string asset = SessionState.GetString(Key + "Avatar", "");
        report = new Report { unityVersion = Application.unityVersion, avatarPath = asset, avatarSha256 = Hash(asset),
            playerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.cs"),
            controllerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.cs"),
            windowSha256 = Hash("Assets/Editor/ArdyRoomLocomotionWindow.cs"),
            arrivalTimingSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomArrivalTiming.cs"),
            regressionSha256 = Hash("Assets/Editor/ArdyRoomIntegrationRegression.cs") };
        report.roomScene = SessionState.GetString(Key + "Scene", "");
        var editGeometry = JsonUtility.FromJson<CarpetGeometrySamples>(SessionState.GetString(Key + "EditGeometry", "{}"));
        if (editGeometry?.values != null) report.carpetGeometrySamples.AddRange(editGeometry.values);
        report.carpetGeometrySamples.AddRange(CaptureCarpetGeometry("entered-play-before-nav"));
        if (!string.IsNullOrEmpty(report.roomScene))
        {
            report.roomSceneSha256 = Hash(report.roomScene);
            report.scope = "Actual geometry-only room scene opened unsaved in disposable project; real imported VRM with actual Animator and configured VRM LateUpdate, captured full-body history, real HTTP ARDY generation and guarded playback along an audited flat-floor route. See runtimeMode for direct versus ControlRig setup.";
            report.limitations = "One audited room route, not stairs, all-room coverage, subjective naturalness, moving-user following or delayed-network races. Source room scene is not modified or saved.";
        }
        var root = GameObject.Find("ARDY-Room-Integration-Avatar");
        avatar = root.GetComponent<Vrm10Instance>(); animator = root.GetComponent<Animator>();
        reference = ArdyLocomotionAvatarReference.FromImportedPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(asset).GetComponent<Vrm10Instance>(), asset);
        var runtimeFlag = typeof(Vrm10Instance).GetField("m_useControlRig", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(runtimeFlag != null && !avatar.enabled && !animator.enabled, "Actual rig must initialize from the imported rest pose.");
        report.importedUseControlRig = (bool)runtimeFlag.GetValue(avatar);
        report.forceControlRig = SessionState.GetBool(Key + "ForceControlRig", false);
        disabledVrm = report.disabledVrm = SessionState.GetBool(Key + "DisabledVrm", false);
        runtimeStorage = typeof(Vrm10Instance).GetField("m_runtime", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(runtimeStorage != null, "The regression must inspect Runtime initialization without accessing its lazy getter.");
        useControlRig = !disabledVrm && (report.importedUseControlRig || report.forceControlRig);
        runtimeInitialized = false;
        CaptureBodyPose("placed-before-runtime-construction");
        // Respect the imported mode by default. The optional ControlRig fixture follows the import
        // contract: construct normalized rig at identity in the actual imported T-pose, then place it.
        // This changes setup before any Animator/VRM frame, never measured history or an active pose.
        if (useControlRig)
        {
            Vector3 placedPosition = root.transform.position, placedScale = root.transform.localScale;
            Quaternion placedRotation = root.transform.rotation;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;
            runtimeFlag.SetValue(avatar, true);
            CaptureBodyPose("identity-before-control-rig-construction");
            Check(avatar.Runtime.ControlRig != null, "ControlRig initialization was requested but produced no rig.");
            runtimeInitialized = true;
            CaptureBodyPose("identity-after-control-rig-construction");
            root.transform.localScale = placedScale;
            root.transform.SetPositionAndRotation(placedPosition, placedRotation);
            report.controlRigInitializedAtIdentity = true;
        }
        else if (!disabledVrm)
        {
            Check(avatar.Runtime.ControlRig == null, "Default imported direct-Animator mode unexpectedly created a ControlRig.");
            runtimeInitialized = true;
        }
        report.runtimeMode = disabledVrm ? "Chat configuration: Vrm10Instance disabled, direct Animator -> actual Humanoid, VRM Runtime never initialized"
            : useControlRig ? "ControlRig constructed at identity, then placed in room" : "Imported direct Animator -> actual Humanoid (no ControlRig)";
        CaptureBodyPose(disabledVrm ? "placed-without-runtime-before-animator" : "placed-after-runtime-construction-before-animator");
        avatar.enabled = !disabledVrm;
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Check(animator.runtimeAnimatorController != null, "Actual idle Animator controller is missing.");
        animator.enabled = true; animator.SetInteger("state", 0);
        CaptureBodyPose("animator-enabled-before-first-frame");
        string environmentName = string.IsNullOrEmpty(report.roomScene) ? "ARDY-Room-Integration-Environment" : SessionState.GetString(Key + "Environment", "");
        var environment = GameObject.Find(environmentName);
        Check(environment != null, "The explicitly selected room environment root was not found: " + environmentName);
        string exclusions = SessionState.GetString(Key + "VisualExclusions", "");
        var excludedRoots = new List<Transform>();
        if (!string.IsNullOrEmpty(exclusions))
        {
            excludedRoots = exclusions.Split('|').Select(name => {
                var found = GameObject.Find(name);
                if (found == null) throw new InvalidOperationException("Explicit visual exclusion was not found: " + name);
                return found.transform;
            }).ToList();
        }
        if (disabledVrm)
        {
            // Exercise the actual user entry point; duplicating Configure here missed the
            // earlier window-versus-controller lifecycle mismatch in the real chat scene.
            window = ScriptableObject.CreateInstance<ArdyRoomLocomotionWindow>();
            SetWindowField("avatar", avatar);
            SetWindowField("importedModel", AssetDatabase.LoadAssetAtPath<GameObject>(asset));
            SetWindowField("environmentRoot", environment.transform);
            SetWindowField("endpoint", SessionState.GetString(Key + "Endpoint", ""));
            SetWindowField("excludedVisualRoots", excludedRoots);
            InvokeWindow("BuildSession");
            controller = GetWindowField<ArdyRoomLocomotionController>("controller");
            navigation = GetWindowField<ArdyRoomNavigation>("navigation");
            report.boundViaWindow = controller != null && navigation != null;
            Check(report.boundViaWindow, "The actual user window failed to bind the disabled-VRM chat avatar.");
            CheckDisabledVrmPreserved();
            report.scope = (string.IsNullOrEmpty(report.roomScene) ? "Isolated flat floor" : "Actual geometry-only room scene") +
                "; the real editor window BuildSession binds the disabled-VRM chat configuration. Direct Animator -> actual Humanoid, real captured full-body history, real HTTP ARDY generation and full guarded playback keep VRM disabled and its Runtime uninitialized. Lifecycle rejection fixtures run after successful playback; no manual VRM.Process, SampleAt or synthetic history.";
        }
        else
        {
            var host = new GameObject("ARDY room integration runtime");
            navigation = host.AddComponent<ArdyRoomNavigation>();
            navigation.AgentHeight = (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * root.transform.lossyScale.y + .18f;
            navigation.ExcludedVisualRoots = excludedRoots.ToArray();
            Check(navigation.Build(environment.transform, root.transform, out string reason), reason);
            controller = host.AddComponent<ArdyRoomLocomotionController>();
            controller.Endpoint = SessionState.GetString(Key + "Endpoint", "");
            controller.Configure(avatar, reference, navigation);
        }
        report.excludedVisualRoots = exclusions;
        report.environmentRoot = environmentName;
        report.geometrySourceCount = navigation.SourceCount; report.geometryProxyCount = navigation.GeometryProxyCount;
        report.thinCoverCount = navigation.ThinCoverCount;
        report.thinCoverDiagnostics = navigation.ThinCoverDiagnostics.ToArray();
        report.carpetGeometrySamples.AddRange(CaptureCarpetGeometry("entered-play-after-nav"));
        report.initialYaw = root.transform.eulerAngles.y; report.avatarScale = root.transform.lossyScale.y;
        startPosition = root.transform.position;
        target = root.transform.position + root.transform.forward * 1.2f; target.y = 0;
        string targetArgument = SessionState.GetString(Key + "Target", "");
        if (!string.IsNullOrEmpty(targetArgument)) target = ParseVector(targetArgument);
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        CreateIndependentIdleBaseline();
        precedingArrivalFrame = firstBaselineFrame = null;
        baselineStarted = arrivalObserved = restoredObserved = false;
        playerLoopObservationError = null;
        InstallObservationLoop(true);
        phase = 0; phaseTime = Time.realtimeSinceStartup; lastFrame = Time.frameCount;
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Room integration exceeded its bounded test duration.")); return; }
        if (!EditorApplication.isPlaying || EditorApplication.isPaused || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            if (playerLoopObservationError != null) throw new InvalidOperationException("Actual player-loop arrival observation failed.", playerLoopObservationError);
            if (disabledVrm) CheckDisabledVrmPreserved();
            if (phase == 0)
            {
                if (Time.realtimeSinceStartup - phaseTime < .3f) return;
                CaptureBodyPose("after-initial-frames-before-move");
                if (disabledVrm) Check(animator.GetBoneTransform(HumanBodyBones.Hips) == avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips),
                    "The disabled-VRM fixture must animate the actual Humanoid skeleton directly.");
                report.carpetGeometrySamples.AddRange(CaptureCarpetGeometry("after-initial-frames-before-move"));
                CheckRenderedHistory();
                Vector3 originalScale = avatar.transform.localScale;
                avatar.transform.localScale = new Vector3(-Mathf.Abs(originalScale.x), originalScale.y, originalScale.z);
                bool rejected = false;
                try { controller.Player.CaptureFullBodyHistoryFrame(avatar.transform.rotation); }
                catch (InvalidOperationException) { rejected = true; }
                finally { avatar.transform.localScale = originalScale; }
                Check(rejected, "Negative root scale must be rejected before history projection.");
                controller.MoveTo(target);
                controller.Cancel();
                Check(!controller.IsBusy && !controller.Player.HasPoseOwnership, "Immediate cancellation retained work or body ownership.");
                report.cancellationPreservedRoot = Vector3.Distance(startPosition, avatar.transform.position) < .0001f;
                Check(report.cancellationPreservedRoot, "Cancelled request moved the root.");
                controller.MoveTo(target);
                phase = 1; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 1)
            {
                if (controller.IsMoving && !controller.Player.IsReturningToAnimation)
                {
                    Check(!animator.enabled && (disabledVrm ? !avatar.enabled : avatar.enabled && (avatar.Runtime.ControlRig != null) == useControlRig),
                        "Full playback did not retain the imported runtime mode and pause Animator.");
                    if (report.observedPlaybackFrames == 0) CaptureBodyPose("first-observed-playback-frame");
                    report.observedPlaybackFrames++;
                    report.maximumRootTravel = Mathf.Max(report.maximumRootTravel, Vector3.Distance(startPosition, avatar.transform.position));
                    CheckPlayingHistoryRoundtrip();
                }
                if (controller.Player.IsReturningToAnimation)
                    Check(controller.IsMoving && controller.Player.HasPoseOwnership && animator.enabled,
                        "Returning to the Animator must remain a controlled movement phase with Animator enabled.");
                if (controller.IsBusy || controller.IsMoving) return;
                Check(controller.LastClip != null, "Real HTTP generation failed: " + controller.Status);
                Check(controller.Status.StartsWith("已到达", StringComparison.Ordinal), "Movement did not complete: " + controller.Status);
                clip = controller.LastClip;
                report.realResponseReceived = !string.IsNullOrWhiteSpace(controller.LastResponseJson);
                report.outputFrames = clip.frames.Length;
                Check(report.realResponseReceived && report.observedPlaybackFrames > 10 && report.maximumRootTravel > .7f,
                    "Real HTTP/actual player-loop movement was not observed.");
                report.actualEndpointError = Vector2.Distance(new Vector2(avatar.transform.position.x, avatar.transform.position.z), new Vector2(target.x, target.z));
                report.completedNaturally = report.actualEndpointError <= controller.ArrivalTolerance;
                report.bodyOwnersRestored = animator.enabled && !controller.Player.HasPoseOwnership;
                Check(report.completedNaturally && report.bodyOwnersRestored, "Final root position or body owner restoration failed.");
                SaveWire();
                phase = 4; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 4)
            {
                // Observe actual Animator frames after ownership release as well: a one-frame
                // jump at cleanup would be invisible if the test ended at the arrival event.
                if (Time.realtimeSinceStartup - phaseTime < .3f) return;
                ValidateArrivalObservations();
                // A guard rejection on the next actual LateUpdate must stop before applying that sample.
                int guardCalls = 0;
                controller.Player.Play(clip, (previous, next) => ++guardCalls == 1 ? null : "integration test obstacle");
                blockedPosition = avatar.transform.position;
                phase = 2; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 2)
            {
                if (Time.realtimeSinceStartup - phaseTime < .15f) return;
                report.guardBlockedBeforeMovement = !controller.Player.HasPoseOwnership && animator.enabled &&
                    Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f && controller.Player.LastBlockedReason == "integration test obstacle";
                Check(report.guardBlockedBeforeMovement, "RootStepGuard moved into an obstacle or failed to release Animator.");
                controller.Player.Play(clip);
                long otherRevision = controller.Player.PlaybackRevision;
                controller.Cancel();
                report.externalOwnerPreserved = controller.Player.HasPoseOwnership && controller.Player.PlaybackRevision == otherRevision;
                Check(report.externalOwnerPreserved, "Controller cancellation stopped an unowned external clip.");
                controller.Player.Stop();
                endpointFixture = HeldEndpointFixture("arrival-interruption-fixture");
                replacementFixture = HeldEndpointFixture("arrival-replacement-fixture");
                controller.Player.Play(endpointFixture);
                phase = 5; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 5)
            {
                if (!controller.Player.CompletedNaturally) return;
                fixtureRevision = controller.Player.PlaybackRevision;
                blockedPosition = avatar.transform.position; blockedRotation = avatar.transform.rotation;
                controller.Player.ReturnToAnimation(.4f);
                Check(controller.Player.PlaybackRevision == fixtureRevision && controller.Player.IsReturningToAnimation,
                    "Beginning return unexpectedly replaced its clip revision.");
                controller.Cancel();
                report.externalReturnPreserved = controller.Player.HasPoseOwnership && controller.Player.IsReturningToAnimation &&
                    controller.Player.PlaybackRevision == fixtureRevision;
                Check(report.externalReturnPreserved, "Controller cancelled another caller's Animator return.");
                phase = 6; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 6)
            {
                if (Time.realtimeSinceStartup - phaseTime < .08f) return;
                Check(controller.Player.IsReturningToAnimation, "Cancellation fixture did not span actual return frames.");
                controller.Player.Stop();
                report.returnCancellationPreservedRoot = !controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation && animator.enabled &&
                    Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f && Quaternion.Angle(blockedRotation, avatar.transform.rotation) < .05f;
                Check(report.returnCancellationPreservedRoot, "Stopping during return retained work or moved the reached root.");
                controller.Player.Play(endpointFixture);
                phase = 7; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 7)
            {
                if (!controller.Player.CompletedNaturally) return;
                controller.Player.ReturnToAnimation(.4f);
                fixtureRevision = controller.Player.PlaybackRevision;
                blockedPosition = avatar.transform.position; blockedRotation = avatar.transform.rotation;
                phase = 8; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 8)
            {
                if (Time.realtimeSinceStartup - phaseTime < .08f) return;
                Check(controller.Player.IsReturningToAnimation, "Replacement fixture did not span actual return frames.");
                controller.Player.Play(replacementFixture);
                Check(controller.Player.PlaybackRevision != fixtureRevision && !controller.Player.IsReturningToAnimation,
                    "A replacement clip retained the previous Animator return.");
                fixtureRevision = controller.Player.PlaybackRevision;
                phase = 9; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 9)
            {
                if (Time.realtimeSinceStartup - phaseTime < .45f) return;
                report.returnReplacementPreserved = controller.Player.Clip == replacementFixture && controller.Player.PlaybackRevision == fixtureRevision &&
                    controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation && !animator.enabled &&
                    Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f;
                Check(report.returnReplacementPreserved, "A previous return completed over the replacement clip or moved its root.");
                controller.Player.ReturnToAnimation(.4f);
                phase = 10; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 10)
            {
                if (Time.realtimeSinceStartup - phaseTime < .08f) return;
                Check(controller.Player.IsReturningToAnimation, "External-root fixture did not span actual return frames.");
                avatar.transform.position += new Vector3(.03f, 0, 0);
                avatar.transform.rotation = Quaternion.AngleAxis(2, Vector3.up) * avatar.transform.rotation;
                blockedPosition = avatar.transform.position; blockedRotation = avatar.transform.rotation;
                phase = 11; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 11)
            {
                if (Time.realtimeSinceStartup - phaseTime < .15f) return;
                report.externalRootDuringReturnPreserved = !controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation && animator.enabled &&
                    Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f && Quaternion.Angle(blockedRotation, avatar.transform.rotation) < .05f;
                Check(report.externalRootDuringReturnPreserved, "Animator return overwrote an external root placement or retained ownership.");
                controller.Player.Play(endpointFixture);
                phase = 12; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 12)
            {
                if (!controller.Player.CompletedNaturally) return;
                controller.Player.ReturnToAnimation(.4f);
                phase = 13; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 13)
            {
                if (Time.realtimeSinceStartup - phaseTime < .08f) return;
                Check(controller.Player.IsReturningToAnimation && animator.enabled, "External Animator-owner fixture did not enter a live return.");
                blockedPosition = avatar.transform.position;
                animator.enabled = false;
                var externalHand = avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftHand);
                externalHand.localRotation = Quaternion.AngleAxis(15, Vector3.up) * externalHand.localRotation;
                externalHandRotation = externalHand.localRotation;
                phase = 14; phaseTime = Time.realtimeSinceStartup;
            }
            else if (phase == 14)
            {
                if (Time.realtimeSinceStartup - phaseTime < .15f) return;
                report.externalAnimatorDisablePreserved = !controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation && !animator.enabled &&
                    Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f;
                Check(report.externalAnimatorDisablePreserved, "Aborting return undid another owner's Animator disable or retained body ownership.");
                report.externalPoseDuringReturnPreserved = Quaternion.Angle(externalHandRotation,
                    avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftHand).localRotation) < .05f;
                Check(report.externalPoseDuringReturnPreserved, "Aborting return overwrote a bone pose applied by the external owner.");
                animator.enabled = true;
                BeginRemainingControllerChecks();
            }
            else if (phase == 3)
            {
                if (Time.realtimeSinceStartup - phaseTime < .15f) return;
                report.inactiveAvatarStopped = !controller.IsBusy && !controller.Player.HasPoseOwnership &&
                    Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f;
                Check(report.inactiveAvatarStopped, "Deactivating the avatar GameObject failed to cancel before movement.");
                avatar.gameObject.SetActive(true);
                // Temporarily change the component mode and invoke the real projection path
                // synchronously, before Unity can run Start/Update or initialize its Runtime.
                // The player must reject a change to its bound mode, never adapt it silently.
                avatar.enabled = true;
                try
                {
                    try { controller.Player.CaptureFullBodyHistoryFrame(avatar.transform.rotation); }
                    catch (InvalidOperationException error)
                    {
                        report.runtimeStateChangeRejected = error.Message.IndexOf("VRM enabled state changed", StringComparison.Ordinal) >= 0;
                    }
                    Vector3 returnGround = startPosition + Vector3.up * reference.groundY * avatar.transform.lossyScale.y;
                    try { controller.MoveTo(returnGround); }
                    catch (InvalidOperationException error)
                    {
                        report.controllerModeChangeRejected = error.Message.IndexOf("VRM 更新模式", StringComparison.Ordinal) >= 0;
                    }
                }
                finally { avatar.enabled = false; }
                Check(report.runtimeStateChangeRejected, "Changing the bound VRM component mode was accepted by history projection.");
                Check(report.controllerModeChangeRejected && !controller.IsBusy, "The room controller accepted a new request after its bound VRM mode changed.");
                Check(Vector3.Distance(blockedPosition, avatar.transform.position) < .0001f, "Runtime-mode rejection changed the root.");
                CheckDisabledVrmPreserved();
                FinishControllerChecks();
            }
        }
        catch (Exception error) { Finish(error); }
    }

    private static void BeginRemainingControllerChecks()
    {
        if (!disabledVrm) { FinishControllerChecks(); return; }
        avatar.gameObject.SetActive(false);
        try { InvokeWindow("BuildSession"); }
        catch (InvalidOperationException) { report.inactiveAvatarBindRejected = true; }
        finally { avatar.gameObject.SetActive(true); }
        Check(report.inactiveAvatarBindRejected, "The user window accepted a deactivated avatar GameObject.");
        // A disabled component is valid, but disabling the character object is not.
        // Keep it inactive across a real player-loop frame so the controller must cancel.
        Vector3 returnGround = startPosition + Vector3.up * reference.groundY * avatar.transform.lossyScale.y;
        controller.MoveTo(returnGround);
        Check(controller.IsBusy, "Inactive-avatar fixture did not begin a real request.");
        blockedPosition = avatar.transform.position;
        avatar.gameObject.SetActive(false);
        phase = 3; phaseTime = Time.realtimeSinceStartup;
    }

    private static ArdyLocomotionClip HeldEndpointFixture(string id)
    {
        var held = ArdyLocomotionClip.Parse(JsonUtility.ToJson(clip));
        int last = held.frames.Length - 1;
        held.id = id;
        held.frames = new[] { held.frames[last], held.frames[last] };
        held.targets = new[] { held.targets[last], held.targets[last] };
        held.rotationClip.frames = new[] { held.rotationClip.frames[last], held.rotationClip.frames[last] };
        held.sourceOrigin = held.frames[0].rootPosition; held.sourceOrigin.y = 0;
        Vector3 forward = held.rotationClip.SampleDelta(0, 0) * Vector3.forward;
        held.sourceHeadingDegrees = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        held.Validate();
        return held;
    }

    private static void CreateIndependentIdleBaseline()
    {
        idleBaselineTransforms = new Dictionary<Transform, Transform>();
        Transform copiedRoot = Copy(avatar.transform, null);
        copiedRoot.name = "ARDY independent Animator arrival baseline";
        copiedRoot.gameObject.hideFlags = HideFlags.DontSave;
        idleBaseline = copiedRoot.gameObject.AddComponent<Animator>();
        idleBaseline.enabled = false;
        idleBaseline.avatar = animator.avatar;
        idleBaseline.runtimeAnimatorController = animator.runtimeAnimatorController;
        idleBaseline.applyRootMotion = false;
        idleBaseline.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        idleBaseline.updateMode = animator.updateMode;
        idleBaseline.speed = animator.speed;

        Transform Copy(Transform source, Transform parent)
        {
            var value = new GameObject(source.name).transform;
            value.SetParent(parent, false);
            value.localPosition = source.localPosition; value.localRotation = source.localRotation; value.localScale = source.localScale;
            idleBaselineTransforms.Add(source, value);
            foreach (Transform child in source) Copy(child, value);
            return value;
        }
    }

    private static void StartIndependentIdleBaseline()
    {
        Check(!useControlRig, "This independent actual-Humanoid baseline is limited to the verified direct-Animator configurations.");
        Check(animator.enabled && animator.layerCount > 0 && !animator.IsInTransition(0),
            "The actual idle controller must be in a stable state when the arrival baseline is synchronized.");
        idleBaseline.transform.SetPositionAndRotation(avatar.transform.position, avatar.transform.rotation);
        idleBaseline.transform.localScale = avatar.transform.lossyScale;
        idleBaseline.enabled = true;
        foreach (var parameter in animator.parameters)
        {
            if (parameter.type == AnimatorControllerParameterType.Float) idleBaseline.SetFloat(parameter.nameHash, animator.GetFloat(parameter.nameHash));
            if (parameter.type == AnimatorControllerParameterType.Int) idleBaseline.SetInteger(parameter.nameHash, animator.GetInteger(parameter.nameHash));
            if (parameter.type == AnimatorControllerParameterType.Bool) idleBaseline.SetBool(parameter.nameHash, animator.GetBool(parameter.nameHash));
        }
        for (int layer = 0; layer < animator.layerCount; layer++)
        {
            var state = animator.GetCurrentAnimatorStateInfo(layer);
            idleBaseline.SetLayerWeight(layer, animator.GetLayerWeight(layer));
            idleBaseline.Play(state.fullPathHash, layer, state.normalizedTime);
        }
        baselineStarted = true;
    }

    private static void InstallObservationLoop(bool install)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        Rewrite(ref loop);
        PlayerLoop.SetPlayerLoop(loop);

        void Rewrite(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return;
            var children = system.subSystemList.Where(child => child.type != typeof(ArdyRoomIntegrationRegression)).ToList();
            for (int i = 0; i < children.Count; i++) { var child = children[i]; Rewrite(ref child); children[i] = child; }
            if (install && system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                children.Add(new PlayerLoopSystem { type = typeof(ArdyRoomIntegrationRegression), updateDelegate = ObserveArrivalFrame });
            system.subSystemList = children.ToArray();
        }
    }

    private static void ObserveArrivalFrame()
    {
        if (report == null || controller == null || avatar == null || (phase != 1 && phase != 4) || playerLoopObservationError != null) return;
        try
        {
            var player = controller.Player;
            if (player == null || player.Clip == null || !player.HasPoseOwnership && !arrivalObserved) return;
            var frame = CaptureArrivalFrame();
            if (precedingArrivalFrame != null)
                PoseDifference(precedingArrivalFrame.actual, frame.actual, out frame.adjacentRotationStepDegrees, out frame.adjacentPositionStepMeters);
            if (frame.returning && !arrivalObserved)
            {
                Check(precedingArrivalFrame != null, "Arrival observation missed the generated pose before return.");
                arrivalObserved = true; returnStartedAt = frame.realtime;
                arrivalRevision = frame.revision; returnRootPosition = frame.rootPosition; returnRootRotation = frame.rootRotation;
                report.firstReturnRotationStepDegrees = frame.adjacentRotationStepDegrees;
                report.firstReturnPositionStepMeters = frame.adjacentPositionStepMeters;
                report.observedReturnClipSeconds = frame.clipSeconds;
                StartIndependentIdleBaseline();
            }
            if (arrivalObserved)
            {
                Check(frame.revision == arrivalRevision, "Natural Animator handoff replaced the clip revision.");
                report.maximumReturnRootDrift = Mathf.Max(report.maximumReturnRootDrift, Vector3.Distance(returnRootPosition, frame.rootPosition));
                Check(Quaternion.Angle(returnRootRotation, frame.rootRotation) < .05f, "Animator handoff changed the reached root heading.");
                report.maximumReturnRotationStepDegrees = Mathf.Max(report.maximumReturnRotationStepDegrees, frame.adjacentRotationStepDegrees);
                report.maximumReturnPositionStepMeters = Mathf.Max(report.maximumReturnPositionStepMeters, frame.adjacentPositionStepMeters);
                if (frame.returning)
                {
                    report.observedReturnFrames++;
                    Check(frame.animatorEnabled && frame.hasPoseOwnership && controller.IsMoving,
                        "An actual rendered return frame lost Animator evaluation or movement ownership.");
                }
                if (baselineStarted && frame.independentIdle.Count > 0 && firstBaselineFrame == null) firstBaselineFrame = frame;
                if (frame.returned && !frame.hasPoseOwnership)
                {
                    if (!restoredObserved)
                    {
                        report.returnDuration = frame.realtime - returnStartedAt;
                        report.firstRestoredRotationStepDegrees = frame.adjacentRotationStepDegrees;
                        report.firstRestoredPositionStepMeters = frame.adjacentPositionStepMeters;
                        restoredObserved = true;
                    }
                    report.observedRestoredFrames++;
                    report.maximumRestoredBaselineRotationErrorDegrees = Mathf.Max(report.maximumRestoredBaselineRotationErrorDegrees, frame.baselineRotationErrorDegrees);
                    report.maximumRestoredBaselinePositionErrorMeters = Mathf.Max(report.maximumRestoredBaselinePositionErrorMeters, frame.baselinePositionErrorMeters);
                    Check(frame.animatorEnabled, "The Animator was not enabled after return completed.");
                }
            }
            if (arrivalObserved || player.TimeSeconds >= player.PlaybackEndSeconds - .18f)
            {
                Check(report.arrivalFrames.Count < 300, "Arrival frame observation exceeded its bounded budget.");
                frame.stage = frame.returning ? "returning-to-current-Animator" : frame.returned ? "Animator-after-return" : "generated-ending";
                report.arrivalFrames.Add(frame);
            }
            precedingArrivalFrame = frame;
        }
        catch (Exception error) { playerLoopObservationError = error; }
    }

    private static ArrivalFrame CaptureArrivalFrame()
    {
        var player = controller.Player;
        var value = new ArrivalFrame {
            frame = Time.frameCount, realtime = Time.realtimeSinceStartup, deltaTime = Time.deltaTime,
            clipSeconds = player.TimeSeconds, returning = player.IsReturningToAnimation, returned = player.ReturnedToAnimation,
            animatorEnabled = animator.enabled, hasPoseOwnership = player.HasPoseOwnership, revision = player.PlaybackRevision,
            rootPosition = avatar.transform.position, rootRotation = avatar.transform.rotation,
            animatorNormalizedTime = animator.enabled ? animator.GetCurrentAnimatorStateInfo(0).normalizedTime : 0
        };
        foreach (var bone in ArrivalBones)
        {
            Transform actual = avatar.Humanoid.GetBoneTransform(bone);
            if (actual == null) continue;
            value.actual.Add(Pose(bone, actual, avatar.transform));
            if (baselineStarted && idleBaselineTransforms.TryGetValue(actual, out var independent))
                value.independentIdle.Add(Pose(bone, independent, idleBaseline.transform));
        }
        if (value.independentIdle.Count == value.actual.Count)
            PoseDifference(value.actual, value.independentIdle, out value.baselineRotationErrorDegrees, out value.baselinePositionErrorMeters);
        return value;

        ArrivalBone Pose(HumanBodyBones bone, Transform tf, Transform root)
        {
            // Express world displacements in the character's orientation without removing its
            // real avatar scale: the recorded position differences remain physical metres.
            Quaternion inverse = Quaternion.Inverse(root.rotation);
            return new ArrivalBone { bone = bone.ToString(), positionInRoot = inverse * (tf.position - root.position), rotationInRoot = inverse * tf.rotation };
        }
    }

    private static void PoseDifference(List<ArrivalBone> first, List<ArrivalBone> second, out float degrees, out float meters)
    {
        degrees = meters = 0;
        Check(first.Count == second.Count && first.Count >= 15, "Arrival pose lacks the observed torso, arms, hands or legs.");
        for (int i = 0; i < first.Count; i++)
        {
            Check(first[i].bone == second[i].bone, "Arrival pose bone ordering changed.");
            degrees = Mathf.Max(degrees, Quaternion.Angle(first[i].rotationInRoot, second[i].rotationInRoot));
            meters = Mathf.Max(meters, Vector3.Distance(first[i].positionInRoot, second[i].positionInRoot));
        }
    }

    private static void ValidateArrivalObservations()
    {
        var timing = controller.LastArrivalTiming;
        Check(timing != null, "Room playback did not retain its independently audited arrival timing.");
        report.rawClipLastSampleSeconds = clip.LastSampleSeconds;
        report.terminalHoldStartFrame = timing.TerminalHoldStartFrame;
        report.playbackEndFrame = timing.PlaybackEndFrame;
        report.plannedArrivalSeconds = timing.PlannedArrivalSeconds;
        report.selectedPlaybackEndSeconds = controller.Player.PlaybackEndSeconds;
        report.plannedArrivalToReturnSeconds = report.observedReturnClipSeconds - report.plannedArrivalSeconds;
        report.skippedTailSeconds = clip.LastSampleSeconds - report.selectedPlaybackEndSeconds;
        // Independently derive the terminal requested hold from the saved HTTP request, so a
        // selector that starts at an earlier pause/visit cannot certify itself through its API.
        var request = Newtonsoft.Json.Linq.JObject.Parse(controller.LastRequestJson);
        var requestedTargets = (Newtonsoft.Json.Linq.JArray)request["targets"];
        int terminal = requestedTargets.Count - 1;
        while (terminal > 0 && Newtonsoft.Json.Linq.JToken.DeepEquals(requestedTargets[terminal - 1], requestedTargets[requestedTargets.Count - 1])) terminal--;
        Check(terminal == report.terminalHoldStartFrame, "Arrival timing began before the request's final constant position and heading segment.");
        Check(Mathf.Abs(report.plannedArrivalSeconds - terminal / clip.rotationClip.fps) < .0001f,
            "Reported planned arrival does not correspond to the saved request timing.");
        report.playbackEndRespected = Mathf.Abs(report.observedReturnClipSeconds - report.selectedPlaybackEndSeconds) < .0001f &&
            Mathf.Abs(report.selectedPlaybackEndSeconds - report.playbackEndFrame / clip.rotationClip.fps) < .0001f &&
            report.selectedPlaybackEndSeconds <= clip.LastSampleSeconds && report.plannedArrivalToReturnSeconds >= 0;
        Check(report.playbackEndRespected, "Actual playback did not begin Animator return at the selected post-arrival frame.");
        var raw = ArdyLocomotionClip.Parse(controller.LastResponseJson);
        report.rawResponseClipPreserved = raw.frames.Length == clip.frames.Length && raw.frames.Length == requestedTargets.Count &&
            JsonUtility.ToJson(raw) == JsonUtility.ToJson(clip);
        Check(report.rawResponseClipPreserved, "Selecting an earlier playback endpoint altered or discarded the raw HTTP source clip.");
        Check(arrivalObserved && restoredObserved && report.observedReturnFrames >= 8 && report.observedRestoredFrames >= 8,
            "Natural arrival did not include a multi-frame return and post-release Animator observation.");
        Check(firstBaselineFrame != null && precedingArrivalFrame != null, "The independently animated idle baseline was not observed.");
        PoseDifference(firstBaselineFrame.independentIdle, precedingArrivalFrame.independentIdle,
            out report.animatedBaselineRotationChangeDegrees, out report.animatedBaselinePositionChangeMeters);
        report.animatedBaselineNormalizedTimeAdvance = precedingArrivalFrame.animatorNormalizedTime - firstBaselineFrame.animatorNormalizedTime;
        Check(report.animatedBaselineNormalizedTimeAdvance > .02f &&
            (report.animatedBaselineRotationChangeDegrees > .05f || report.animatedBaselinePositionChangeMeters > .0002f),
            "The baseline idle did not actually move over time; a frozen snapshot could pass this fixture.");
        report.arrivalTransitionContinuous = report.returnDuration >= .25f && report.returnDuration <= .75f &&
            report.firstReturnRotationStepDegrees < 1 && report.firstReturnPositionStepMeters < .005f &&
            report.maximumReturnRotationStepDegrees < 20 && report.maximumReturnPositionStepMeters < .09f &&
            report.firstRestoredRotationStepDegrees < 10 && report.firstRestoredPositionStepMeters < .05f && report.maximumReturnRootDrift < .0001f;
        Check(report.arrivalTransitionContinuous, "Actual rendered arrival contains an abrupt pose handoff or root drift; see arrivalFrames.");
        report.arrivalMatchesDynamicAnimator = report.maximumRestoredBaselineRotationErrorDegrees < .5f && report.maximumRestoredBaselinePositionErrorMeters < .003f;
        Check(report.arrivalMatchesDynamicAnimator, "Arrival did not converge to the independently advancing real Animator, including frames after release.");
        if (idleBaseline != null) idleBaseline.enabled = false;
    }

    private static void FinishControllerChecks()
    {
        controller.enabled = false;
        Check(!controller.IsBusy && !controller.Player.HasPoseOwnership && animator.enabled, "Controller OnDisable retained its body work.");
        Finish(null);
    }

    private static void CheckDisabledVrmPreserved()
    {
        report.disabledVrmPreserved = avatar != null && !avatar.enabled;
        report.runtimeStayedUninitialized = runtimeStorage != null && runtimeStorage.GetValue(avatar) == null;
        Check(report.disabledVrmPreserved, "The disabled-VRM chat configuration was changed during binding/generation/playback.");
        Check(report.runtimeStayedUninitialized, "The disabled-VRM path constructed VRM Runtime through a lazy property.");
    }

    private static void SetWindowField(string name, object value)
    {
        var field = typeof(ArdyRoomLocomotionWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(field != null, "The actual room window field is missing: " + name);
        field.SetValue(window, value);
    }

    private static T GetWindowField<T>(string name) where T : class
    {
        var field = typeof(ArdyRoomLocomotionWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(field != null, "The actual room window field is missing: " + name);
        return field.GetValue(window) as T;
    }

    private static void InvokeWindow(string name)
    {
        var method = typeof(ArdyRoomLocomotionWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(method != null, "The actual room window entry point is missing: " + name);
        try { method.Invoke(window, null); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { throw error.InnerException; }
    }

    private static void CheckRenderedHistory()
    {
        Quaternion anchor = Quaternion.Euler(0, 12, 0);
        var history = controller.Player.CaptureFullBodyHistoryFrame(anchor);
        Check(history.globalRotations.Length == 27, "History must include the complete Core27 skeleton.");
        foreach (var bone in new[] { HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightFoot, HumanBodyBones.LeftHand })
        {
            string source = bone == HumanBodyBones.Hips ? "Hips" : bone == HumanBodyBones.LeftUpperLeg ? "LeftUpLeg" : bone == HumanBodyBones.RightFoot ? "RightFoot" : "LeftHand";
            Quaternion projected = history.globalRotations[Array.IndexOf(ArdyLiveProtocol.JointNames, source)];
            Quaternion recovered = anchor * projected * reference.Get(bone).rotationInRoot;
            Check(Quaternion.Angle(recovered, avatar.Humanoid.GetBoneTransform(bone).rotation) < .1f, "History failed to preserve actually rendered " + bone);
        }
        Check(Quaternion.Angle(history.globalRotations[11], history.globalRotations[10]) < .05f &&
            Quaternion.Angle(history.globalRotations[18], history.globalRotations[16]) < .05f, "Unobserved Core hand endpoints must inherit the hand.");
    }

    private static void CheckPlayingHistoryRoundtrip()
    {
        var playing = controller.Player.Clip;
        var history = controller.Player.CaptureFullBodyHistoryFrame(Quaternion.Euler(0, report.initialYaw, 0));
        foreach (string name in new[] { "Hips", "LeftUpLeg", "RightFoot", "LeftHand" })
        {
            int index = Array.IndexOf(ArdyLiveProtocol.JointNames, name);
            float error = Quaternion.Angle(history.globalRotations[index], playing.rotationClip.SampleDelta(index, controller.Player.TimeSeconds));
            report.maximumRenderedSourceRotationRoundtripDegrees = Mathf.Max(report.maximumRenderedSourceRotationRoundtripDegrees, error);
            Check(error < .5f, "Play -> actual VRM Humanoid -> Capture source rotation changed " + name + " by " + error + " degrees.");
        }
    }

    private static void SaveWire()
    {
        string output = SessionState.GetString(Key + "Output", "");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "request.json"), controller.LastRequestJson);
        File.WriteAllText(Path.Combine(output, "response.json"), controller.LastResponseJson);
        var request = Newtonsoft.Json.Linq.JObject.Parse(controller.LastRequestJson);
        report.historyFrames = request["initialHistory"]["frames"].Count();
        report.historyRootHeight = (float)request["initialHistory"]["frames"][0]["rootPosition"]["y"];
        Check(report.historyFrames == 16 && report.historyRootHeight > .3f, "Wire lacks actually grounded full-body history.");
    }

    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        InstallObservationLoop(false);
        if (report == null) report = new Report { unityVersion = Application.unityVersion };
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        report.lastControllerStatus = controller == null ? null : controller.Status;
        CaptureBodyPose("finish-before-release");
        report.carpetGeometrySamples.AddRange(CaptureCarpetGeometry("finish-before-release"));
        if (navigation != null)
        {
            report.thinCoverCount = navigation.ThinCoverCount;
            report.thinCoverDiagnostics = navigation.ThinCoverDiagnostics.ToArray();
        }
        if (controller != null) controller.Cancel();
        if (navigation != null) navigation.Clear();
        if (error != null) Debug.LogException(error);
        string output = SessionState.GetString(Key + "Output", "Logs/ardy-room-integration");
        Directory.CreateDirectory(output);
        // SaveWire already preserved the completed request/response pair before lifecycle
        // failure fixtures issue later requests. Do not replace one half with a cancelled run.
        if (!report.realResponseReceived && controller != null && !string.IsNullOrEmpty(controller.LastRequestJson)) File.WriteAllText(Path.Combine(output, "request.json"), controller.LastRequestJson);
        if (!report.realResponseReceived && controller != null && !string.IsNullOrEmpty(controller.LastResponseJson)) File.WriteAllText(Path.Combine(output, "response.json"), controller.LastResponseJson);
        if (controller != null && !string.IsNullOrEmpty(controller.LastErrorResponseJson)) File.WriteAllText(Path.Combine(output, "error-response.json"), controller.LastErrorResponseJson);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        if (idleBaseline != null) { UnityEngine.Object.DestroyImmediate(idleBaseline.gameObject); idleBaseline = null; }
        if (window != null) { UnityEngine.Object.DestroyImmediate(window); window = null; }
        SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1);
        SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        else EditorApplication.delayCall += FinalizeBatch;
    }

    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.Exit(SessionState.GetInt(Key + "ExitCode", 1));
    }

    private static string SelectAvatar()
    {
        string requested = Argument("-ardyRoomAvatar", "");
        var paths = string.IsNullOrEmpty(requested) ? AssetDatabase.GetAllAssetPaths()
            .Where(p => p.StartsWith("Assets/Model/", StringComparison.Ordinal) && p.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p)
            : new[] { requested }.OrderBy(p => p);
        foreach (var path in paths)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null && prefab.GetComponent<Vrm10Instance>() != null && prefab.GetComponent<Animator>() != null) return path;
        }
        throw new InvalidOperationException("No usable current imported VRM was found.");
    }
    private static void CaptureBodyPose(string stage)
    {
        if (report == null || avatar == null || reference == null) return;
        var actual = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
        var animated = animator != null && animator.avatar != null && animator.avatar.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        var rig = runtimeInitialized ? avatar.Runtime.ControlRig : null;
        var rigHips = rig?.GetBoneTransform(HumanBodyBones.Hips);
        float support = avatar.transform.position.y + reference.groundY * avatar.transform.lossyScale.y;
        report.bodyPoseSamples.Add(new BodyPoseSample {
            stage = stage, frame = Time.frameCount, realtime = Time.realtimeSinceStartup,
            animatorEnabled = animator != null && animator.enabled, vrmEnabled = avatar.enabled,
            runtimeInitialized = runtimeInitialized, expectedSupportY = support,
            actualHipsAboveSupport = actual == null ? 0 : actual.position.y - support,
            animatorAvatar = animator == null || animator.avatar == null ? null : animator.avatar.name,
            animatorTargetsActualHumanoid = animated == actual, animatorTargetsControlRig = rigHips != null && animated == rigHips,
            avatarRoot = CaptureTransform(avatar.transform), actualHips = CaptureTransform(actual), animatorHips = CaptureTransform(animated),
            controlRigHips = CaptureTransform(rigHips), controlRigRoot = CaptureTransform(rigHips == null ? null : rigHips.parent)
        });
    }
    private static BodyTransformSample CaptureTransform(Transform value)
    {
        if (value == null) return null;
        return new BodyTransformSample { name = value.name, parent = value.parent == null ? null : value.parent.name,
            worldPosition = value.position, localPosition = value.localPosition, lossyScale = value.lossyScale, localScale = value.localScale,
            worldRotation = value.rotation, localRotation = value.localRotation };
    }
    private static List<CarpetGeometrySample> CaptureCarpetGeometry(string stage)
    {
        var result = new List<CarpetGeometrySample>();
        string room = SessionState.GetString(Key + "Scene", "");
        if (string.IsNullOrEmpty(room)) return result;
        var environment = GameObject.Find(SessionState.GetString(Key + "Environment", ""));
        if (environment == null) return result;
        foreach (var renderer in environment.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer.name.IndexOf("carpet", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter == null ? null : filter.sharedMesh;
            var tf = renderer.transform;
            var ancestors = new List<string>();
            for (var parent = tf; parent != null; parent = parent.parent)
                foreach (var owner in parent.GetComponents<Animator>()) ancestors.Add(TransformPath(parent) + " enabled=" + owner.enabled);
            result.Add(new CarpetGeometrySample {
                stage = stage, frame = Time.frameCount, realtime = Time.realtimeSinceStartup, isPlaying = Application.isPlaying,
                path = TransformPath(tf), rendererInstanceId = renderer.GetInstanceID(), enabled = renderer.enabled,
                activeInHierarchy = renderer.gameObject.activeInHierarchy, isPartOfStaticBatch = renderer.isPartOfStaticBatch,
                sharedMeshName = mesh == null ? null : mesh.name, sharedMeshAssetPath = mesh == null ? null : AssetDatabase.GetAssetPath(mesh),
                meshInstanceId = mesh == null ? 0 : mesh.GetInstanceID(), isReadable = mesh != null && mesh.isReadable,
                vertexCount = mesh == null ? 0 : mesh.vertexCount, subMeshCount = mesh == null ? 0 : mesh.subMeshCount,
                position = tf.position, localPosition = tf.localPosition, rotation = tf.rotation, localScale = tf.localScale,
                lossyScale = tf.lossyScale, localToWorld = tf.localToWorldMatrix, rendererBounds = renderer.bounds,
                sharedMeshBounds = mesh == null ? default : mesh.bounds, ancestorAnimators = ancestors.ToArray()
            });
        }
        return result;
    }
    private static string TransformPath(Transform value)
    {
        string path = value.name;
        while (value.parent != null) { value = value.parent; path = value.name + "/" + path; }
        return path;
    }
    private static string Argument(string key, string fallback)
    {
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    private static Vector3 ParseVector(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 3) throw new ArgumentException("Expected one comma-separated x,y,z coordinate argument.");
        var parsed = new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
        if (float.IsNaN(parsed.x) || float.IsNaN(parsed.y) || float.IsNaN(parsed.z) || float.IsInfinity(parsed.x) || float.IsInfinity(parsed.y) || float.IsInfinity(parsed.z))
            throw new ArgumentException("Room coordinates must be finite.");
        return parsed;
    }
    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(Path.GetFullPath(path)))
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static void Check(bool passed, string reason)
    {
        if (report != null) report.checks++;
        if (!passed) throw new InvalidOperationException(reason);
    }
}
