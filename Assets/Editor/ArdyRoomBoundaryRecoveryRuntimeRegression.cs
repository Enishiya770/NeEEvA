using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

/// <summary>One real native recovery candidate in the existing copied SailingMoon room.</summary>
[InitializeOnLoad]
public static class ArdyRoomBoundaryRecoveryRuntimeRegression
{
    private const string Key = "NeEEvA.ARDY.BoundaryRecoveryRuntime.";
    private const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";
    private const string AvatarPath = "Assets/Model/NeEEvA.vrm";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const string AvatarName = "ARDY isolated boundary recovery avatar";
    private static readonly Vector3 RecordedAvatarGround = new Vector3(-2.67963767f, -.69721955f, -4.99569035f);
    [Serializable] private sealed class Sample
    {
        public int frame, operation;
        public string state, reason, stage, status;
        public bool recoveryActive, playing, returning, poseOwned, animatorEnabled;
        public float seconds, playerSeconds, rootStep, travelFromStart;
        public Vector3 root, avatarGround, hips;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", nativeOutcome = "not-submitted", error, unityVersion;
        public string scope = "One real ARDY /v1/locomotion/generate recovery request in the existing copied SailingMoon geometry, actual imported VRM skeleton and project Animator. Reuses the recorded ground position with fresh idle history and controlled 90-degree root yaw; it does not reconstruct the unrecorded historical moving pose. No repeated native retries, Chat/ASR/TTS, main scene changes, or subjective naturalness claim.";
        public string sceneShaBefore, sceneShaAfter, navigationSha256, controllerSha256, recoverySha256, harnessSha256;
        public int submittedOperations, operation, requestCount, renderedFrames, ownershipChecks;
        public bool startInvalid, recoveryAccepted, everPlayed, actualRootStartAdmitted, poseReleased, animatorRestored, vrmRuntimeStayedDisabled;
        public float duration, recoverySeconds, maximumRootStep, maximumTravel, finalTravel, importedGroundY, avatarScale;
        public Vector3 initialRoot, recordedAvatarGround, observedAvatarGroundBeforeRequest, actualRootAfter, avatarGroundAfter, neutralHorizontal, recoveryTarget;
        public string finalState, finalReason, finalStage, finalStatus;
        public ArdyRoomRouteDiagnostic initialDiagnostic, finalDiagnostic;
        public List<ArdyRoomLocomotionController.SegmentEvidence> segments = new List<ArdyRoomLocomotionController.SegmentEvidence>();
        public List<Sample> samples = new List<Sample>();
    }
    private static Report report;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyRoomNavigation navigation;
    private static ArdyRoomLocomotionController controller;
    private static ArdyLocomotionAvatarReference reference;
    private static FieldInfo vrmRuntime;
    private static Exception observationError;
    private static Vector3 previousRoot;
    private static float started, requestAt, terminalAt;
    private static int phase, lastFrame;
    private static bool observe;

    static ArdyRoomBoundaryRecoveryRuntimeRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                try { Initialize(); } catch (Exception error) { Finish(error); }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) FinalizeBatch();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying)
                FinalizeBatch();
        };
    }

    public static void RunBatch() => RunBatchCore(false);
    public static void RunOwnershipBatch() => RunBatchCore(true);

    private static void RunBatchCore(bool ownershipOnly)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the isolated validation project with -batchmode and without -quit.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("An isolated Play session is already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved validation scene.");
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-boundary-recovery-runtime")));
            SessionState.SetBool(Key + "OwnershipOnly", ownershipOnly);
            SessionState.SetString(Key + "Endpoint", Argument("-ardyRoomEndpoint", "http://127.0.0.1:8093"));
            SessionState.SetString(Key + "SceneHash", Hash(ScenePath));
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            // Keep real authored geometry. No room scripts, cameras, audio/video, or chat
            // services run; only the fixture's controller/player/Animator are later enabled.
            foreach (var root in scene.GetRootGameObjects())
                foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            var living = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Collider>(true)).Single(c => c.name == "リビング床");
            ArdyRoomGeometryPreparation.Prepare(living.transform.root, false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var imported = prefab.GetComponentInChildren<Vrm10Instance>(true);
            var importedReference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, AvatarPath);
            var actor = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene); actor.name = AvatarName;
            float scale = imported.transform.lossyScale.y;
            // RoomTrace.avatarGround already includes the imported support offset.
            // Subtract it exactly once to reconstruct the corresponding root transform.
            actor.transform.position = RecordedAvatarGround - Vector3.up * importedReference.groundY * scale;
            actor.transform.rotation = Quaternion.Euler(0, 90, 0);
            foreach (var behaviour in actor.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (var owner in actor.GetComponentsInChildren<Animator>(true))
            { owner.runtimeAnimatorController = null; owner.applyRootMotion = false; owner.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 110f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error)
        {
            report = new Report { status = "failed", error = error.ToString(), nativeOutcome = "fixture-setup-failed" };
            File.WriteAllText(Path.Combine(Output(), "boundary-recovery-runtime.json"), JsonUtility.ToJson(report, true));
            Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1);
        }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion, recordedAvatarGround = RecordedAvatarGround,
            sceneShaBefore = SessionState.GetString(Key + "SceneHash", ""),
            navigationSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs"),
            controllerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.cs"),
            recoverySha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.BoundaryRecovery.cs"),
            harnessSha256 = Hash("Assets/Editor/ArdyRoomBoundaryRecoveryRuntimeRegression.cs") };
        if (SessionState.GetBool(Key + "OwnershipOnly", false))
        {
            report.scope = "Boundary recovery ownership/cancellation lifecycle in the existing copied SailingMoon room and real configured VRM. Same-frame localhost:1 capability cancellation and injected serial observations; no native motion candidate or pose playback is requested.";
            report.nativeOutcome = "not-requested-ownership-lifecycle";
        }
        avatar = GameObject.Find(AvatarName).GetComponent<Vrm10Instance>(); animator = avatar.GetComponent<Animator>();
        vrmRuntime = typeof(Vrm10Instance).GetField("m_runtime", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(vrmRuntime != null && !avatar.enabled && vrmRuntime.GetValue(avatar) == null, "VRM runtime must remain disabled.");
        reference = ArdyLocomotionAvatarReference.FromImportedPrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath).GetComponentInChildren<Vrm10Instance>(true), AvatarPath);
        report.avatarScale = avatar.transform.lossyScale.y; report.importedGroundY = reference.groundY;
        Vector3 neutral = reference.Get(HumanBodyBones.Hips).positionInRoot * report.avatarScale; neutral.y = 0;
        report.neutralHorizontal = neutral;
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Require(animator.runtimeAnimatorController != null, "Project idle Animator is missing.");
        animator.enabled = true; animator.SetInteger("state", 0);
        var scene = avatar.gameObject.scene;
        var living = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Collider>(true)).Single(c => c.name == "リビング床");
        Transform environment = living.transform.root;
        var host = new GameObject("ARDY boundary recovery runtime host"); SceneManager.MoveGameObjectToScene(host, scene);
        navigation = host.AddComponent<ArdyRoomNavigation>();
        navigation.ExcludedVisualRoots = environment.GetComponentsInChildren<Transform>(true).Where(t => t.name == "水面").ToArray();
        Require(navigation.ExcludedVisualRoots.Length == 1, "Expected the verified non-solid water exclusion.");
        navigation.AgentHeight = Mathf.Max(1.1f, (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * report.avatarScale + .18f);
        Require(navigation.Build(environment, avatar.transform, out string reason), reason);
        controller = host.AddComponent<ArdyRoomLocomotionController>();
        controller.Endpoint = SessionState.GetString(Key + "Endpoint", ""); controller.WalkingSpeed = .2f;
        controller.Configure(avatar, reference, navigation);
        report.initialRoot = previousRoot = avatar.transform.position;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        started = Time.realtimeSinceStartup; requestAt = terminalAt = -1; phase = 0; lastFrame = -1;
        observationError = null; observe = true; InstallObserver(true);
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Boundary recovery fixture exceeded its bounded startup budget.")); return; }
        if (!EditorApplication.isPlaying || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            if (observationError != null) throw observationError;
            Require(Time.realtimeSinceStartup - started < 40f, "One-candidate recovery exceeded its 40-second runtime budget.");
            if (phase == 0)
            {
                if (Time.realtimeSinceStartup - started < .85f) return;
                report.observedAvatarGroundBeforeRequest = AvatarGround();
                report.startInvalid = !navigation.TryValidatePlanStart(AvatarGround(), out _, out _, out var initial);
                report.initialDiagnostic = initial;
                Require(report.startInvalid && initial.code == "navmesh-boundary", "Fixture must begin at the precise recorded boundary fault.");
                if (SessionState.GetBool(Key + "OwnershipOnly", false))
                {
                    report.ownershipChecks = ArdyRoomBoundaryRecoveryOwnershipRegression.RunChecks(controller);
                    Require(controller.Segments.Count == 0 && string.IsNullOrEmpty(controller.LastRequestJson),
                        "Ownership-only mode must not generate a native motion candidate.");
                    Finish(null);
                    return;
                }
                report.submittedOperations++;
                report.recoveryAccepted = controller.TryBeginNavigationRecovery(avatar.transform.position + avatar.transform.forward, out string reason);
                report.operation = controller.OperationRevision; report.recoveryTarget = controller.TargetGround;
                Require(report.recoveryAccepted, "Recovery operation was rejected before a native request: " + reason);
                report.nativeOutcome = "submitted"; requestAt = Time.realtimeSinceStartup; phase = 1;
                return;
            }
            bool terminal = controller.ExecutionState == ArdyRoomExecutionState.Arrived || controller.ExecutionState == ArdyRoomExecutionState.Blocked ||
                controller.ExecutionState == ArdyRoomExecutionState.Failed || controller.ExecutionState == ArdyRoomExecutionState.Cancelled;
            if (controller.IsNavigationRecoveryActive || !terminal) return;
            if (terminalAt < 0) terminalAt = Time.realtimeSinceStartup;
            if (controller.Player.HasPoseOwnership)
            { Require(Time.realtimeSinceStartup - terminalAt < 3f, "Terminal recovery retained body ownership beyond the handoff budget."); return; }
            report.poseReleased = true; report.animatorRestored = animator.enabled;
            report.actualRootStartAdmitted = navigation.TryValidatePlanStart(AvatarGround(), out _, out _, out var final);
            report.finalDiagnostic = final;
            report.requestCount = controller.Segments.Select(s => s.requestId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count();
            if (controller.ExecutionState != ArdyRoomExecutionState.Arrived)
            {
                report.nativeOutcome = report.everPlayed ? "playback-blocked" : "candidate-rejected";
                throw new InvalidOperationException("Native recovery did not succeed: " + controller.Status);
            }
            Require(report.requestCount == 1 && report.submittedOperations == 1, "Expected exactly one native candidate operation.");
            Require(report.everPlayed && report.actualRootStartAdmitted && report.animatorRestored,
                "Recovery must actually play, release to the real Animator and leave an admissible root.");
            report.nativeOutcome = "recovered-and-released";
            Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Observe()
    {
        if (!observe || controller == null || observationError != null) return;
        try
        {
            Require(report.samples.Count < 2500, "Recovery frame evidence exceeded its bounded budget.");
            var player = controller.Player;
            float step = Vector3.Distance(previousRoot, avatar.transform.position);
            float travel = Vector2.Distance(new Vector2(report.initialRoot.x, report.initialRoot.z), new Vector2(avatar.transform.position.x, avatar.transform.position.z));
            report.maximumRootStep = Mathf.Max(report.maximumRootStep, step); report.maximumTravel = Mathf.Max(report.maximumTravel, travel);
            report.everPlayed |= player.IsPlaying; report.renderedFrames++;
            report.vrmRuntimeStayedDisabled = !avatar.enabled && vrmRuntime.GetValue(avatar) == null;
            Require(report.vrmRuntimeStayedDisabled, "Fixture VRM runtime started unexpectedly.");
            Require(travel <= .121f && step <= .036f, "Actual playback left the bounded recovery extent or jumped farther than the step guard.");
            report.samples.Add(new Sample { frame = Time.frameCount, operation = controller.OperationRevision,
                seconds = Time.realtimeSinceStartup - started, state = controller.ExecutionState.ToString(), reason = controller.ExecutionReason.ToString(),
                stage = controller.Stage, status = controller.Status, recoveryActive = controller.IsNavigationRecoveryActive,
                playing = player.IsPlaying, returning = player.IsReturningToAnimation, poseOwned = player.HasPoseOwnership,
                animatorEnabled = animator.enabled, playerSeconds = player.TimeSeconds, root = avatar.transform.position,
                avatarGround = AvatarGround(), hips = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips).position,
                rootStep = step, travelFromStart = travel });
            previousRoot = avatar.transform.position;
        }
        catch (Exception error) { observationError = error; }
    }

    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        observe = false; InstallObserver(false);
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        report.duration = Time.realtimeSinceStartup - started; report.recoverySeconds = requestAt < 0 ? 0 : Time.realtimeSinceStartup - requestAt;
        if (controller != null)
        {
            report.finalState = controller.ExecutionState.ToString(); report.finalReason = controller.ExecutionReason.ToString();
            report.finalStage = controller.Stage; report.finalStatus = controller.Status;
            report.segments = controller.Segments.ToList();
            report.requestCount = report.segments.Select(s => s.requestId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count();
            Write("last-request.json", controller.LastRequestJson); Write("last-response.json", controller.LastResponseJson); Write("last-error-response.json", controller.LastErrorResponseJson);
        }
        if (avatar != null)
        {
            report.actualRootAfter = avatar.transform.position; report.avatarGroundAfter = AvatarGround();
            report.finalTravel = Vector2.Distance(new Vector2(report.initialRoot.x, report.initialRoot.z), new Vector2(avatar.transform.position.x, avatar.transform.position.z));
            if (navigation != null)
                report.actualRootStartAdmitted = navigation.TryValidatePlanStart(AvatarGround(), out _, out _, out report.finalDiagnostic);
        }
        report.sceneShaAfter = Hash(ScenePath);
        if (SessionState.GetBool(Key + "OwnershipOnly", false))
            Write("boundary-recovery-ownership.json", ArdyRoomBoundaryRecoveryOwnershipRegression.LastReportJson);
        if (report.sceneShaBefore != report.sceneShaAfter) { report.status = "failed"; report.error = "Scene file changed unexpectedly. " + report.error; }
        File.WriteAllText(Path.Combine(Output(), "boundary-recovery-runtime.json"), JsonUtility.ToJson(report, true));
        if (error != null) Debug.LogException(error);
        controller?.Cancel(); navigation?.Clear();
        SessionState.SetInt(Key + "ExitCode", report.status == "passed" ? 0 : 1); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.Exit(SessionState.GetInt(Key + "ExitCode", 1));
    }
    private static void InstallObserver(bool install)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop(); Rewrite(ref loop); PlayerLoop.SetPlayerLoop(loop);
        void Rewrite(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return;
            var children = system.subSystemList.Where(c => c.type != typeof(ArdyRoomBoundaryRecoveryRuntimeRegression)).ToList();
            for (int i = 0; i < children.Count; i++) { var child = children[i]; Rewrite(ref child); children[i] = child; }
            if (install && system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                children.Add(new PlayerLoopSystem { type = typeof(ArdyRoomBoundaryRecoveryRuntimeRegression), updateDelegate = Observe });
            system.subSystemList = children.ToArray();
        }
    }
    private static Vector3 AvatarGround() => avatar.transform.position +
        (reference == null ? Vector3.zero : Vector3.up * reference.groundY * avatar.transform.lossyScale.y);
    private static void Require(bool okay, string reason) { if (!okay) throw new InvalidOperationException(reason); }
    private static void Write(string name, string value) { if (!string.IsNullOrEmpty(value)) File.WriteAllText(Path.Combine(Output(), name), value); }
    private static string Output() { string value = SessionState.GetString(Key + "Output", "Logs/ardy-boundary-recovery-runtime"); Directory.CreateDirectory(value); return value; }
    private static string Argument(string name, string fallback) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
}
