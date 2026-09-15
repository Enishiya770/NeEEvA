using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;

/// <summary>Independent real stream/physics/Animator soak test. Run only in the disposable batch project.</summary>
[InitializeOnLoad]
public static class ArdyRoomStreamRuntimeRegression
{
    private const string Key = "NeEEvA.ARDY.StreamRuntime.";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const string RootName = "ARDY Stream Fixture Avatar", FloorName = "ARDY Stream Fixture Floor";
    [Serializable] private sealed class Sample
    {
        public int frame, operation;
        public string phase, state, stage, session;
        public long steps, control, acceptedControl, appliedControl;
        public bool active, playing, owned, returning, acknowledgedBoundKnown;
        public float realtime, unityTime, frameDeltaTime, engineDeltaTime, caseTime, desiredSpeed, measuredSpeed, actualSpeed, headingDegrees, bodyHeadingDegrees,
            rootBodyHeadingError, maximumBoneStep, leftKneeDegrees, rightKneeDegrees, bufferSeconds, generationSeconds,
            bodySampleTime, userDistance, leftFootSpeed, rightFootSpeed;
        public float acknowledgedLastSampleSeconds, controlLatencySeconds;
        public float travelFacingError, predictedLeftContact, predictedRightContact;
        public float hipsHeightAboveGround, hipsStandingRatio, leftFootHeightAboveGround, rightFootHeightAboveGround,
            leftFootRestClearance, rightFootRestClearance, lowHipsRunSeconds, bothFeetBelowRestRunSeconds;
        public int underruns;
        public Vector3 root, userGround, userVelocity, hips, leftFoot, rightFoot;
    }
    [Serializable] private sealed class Case
    {
        public string name, status = "running", error, session;
        public int operation, frames, stopFrames, initialUnderruns, maximumUnderruns, initialRecoveries, finalRecoveries;
        public long initialSteps, finalSteps, initialControl, finalControl;
        public float started, duration, rootTravel, maximumRootStep, maximumProgressGap, maximumBoneStep,
            maximumFootSpeed, maximumRootBodyHeadingError, maximumSpeedError, meanSpeedError,
            stopRootDrift, firstStopBoneStep, maximumStopBoneStep, returnedAfterSeconds, maximumFrameSeconds,
            meanFrameSeconds, effectiveFps, minimumLeftKnee = 180, minimumRightKnee = 180, maximumLeftKnee, maximumRightKnee;
        public float maximumTravelFacingError;
        public float minimumHipsHeight = float.MaxValue, minimumHipsStandingRatio = float.MaxValue,
            maximumLowHipsRunSeconds, minimumLeftFootRestClearance = float.MaxValue,
            minimumRightFootRestClearance = float.MaxValue, maximumBothFeetBelowRestRunSeconds;
        public bool sawPoseReturn, poseReleased, sawExplicitFailure;
    }
    [Serializable] private sealed class MainThird
    {
        public string label;
        public bool observed;
        public float fromSeconds, toSeconds, observedSeconds, meanHipsHeight, minimumHipsHeight,
            maximumHipsHeight, meanStandingRatio, meanActualSpeed, meanDesiredSpeed,
            minimumLeftFootRestClearance, minimumRightFootRestClearance;
        public int samples;
        [NonSerialized] public float hipsSum, ratioSum, speedSum, desiredSum;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, avatar, runtimeAssemblySha256, harnessSha256;
        public string scope = "Direct production continuous-follow controller, real local HTTP, guarded same-level floor, imported Humanoid and running project Animator. Main case lasts at least 60 seconds with changing speed and turning. Explicit stop, dynamic obstacle and unavailable-endpoint failures run separately.";
        public string limits = "No Chat/Qwen/ASR/TTS, main user scene, service restart, subjective naturalness or measured skin contact. Contact values are native model predictions. World foot velocity, actual movement versus facing and speed/body-heading errors are diagnostics, not calibrated foot-slide pass criteria.";
        public string profile = "Continuous 4.5m-radius curved path; requested user speeds change every 10s: .25/.45/.65/1.0/.35/.8m/s. Control updates at 5Hz with replanned same-floor routes; no teleported target during the 60s main interval.";
        public int checks;
        public float initialStandingHipsHeight, importedRestHipsHeight, importedRestLeftFootHeight, importedRestRightFootHeight;
        public float lowHipsStandingFraction = .75f, lowHipsMaximumContinuousSeconds = .3f,
            footBelowRestToleranceMetres = .10f, bothFeetBelowRestMaximumContinuousSeconds = .3f;
        public string qualityGate = "During owned active walking (excluding return), measured hips below 75% of the pre-stream standing height for more than .3s fail. Both foot-bone landmarks more than .10m below their imported neutral floor-relative height for more than .3s fail. These are gross-pose gates, not skin-contact or naturalness scores. Empty planned main thirds are explicitly unobserved.";
        public List<MainThird> mainThirds = new List<MainThird> {
            new MainThird { label = "early", fromSeconds = 0, toSeconds = 20 },
            new MainThird { label = "middle", fromSeconds = 20, toSeconds = 40 },
            new MainThird { label = "late", fromSeconds = 40, toSeconds = 60 }
        };
        public List<Case> cases = new List<Case>();
        public List<Sample> samples = new List<Sample>();
    }
    [Serializable] private sealed class WireSnapshot
    {
        public string caseName, status, stage, reason, session, lastRequestJson, lastResponseJson, lastErrorResponseJson;
        public float capturedAtRealtime, bufferSeconds, generationSeconds;
        public int operation, revision, stepCount, controlRevision, acceptedControlRevision, appliedControlRevision, underruns,
            discardedByController, omittedBySnapshot;
        public bool active, busy, containsInFlightRequest;
        public string scope = "Latest at most 64 controller segment records, including raw response JSON/backend timing and current or failed request evidence. Captured before harness teardown; does not infer success from a submitted request.";
        public ArdyRoomLocomotionController.SegmentEvidence[] segments;
    }
    private static readonly HumanBodyBones[] Observed = { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
        HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
        HumanBodyBones.LeftHand, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes };
    private static Report report;
    private static Case current;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyRoomLocomotionController controller;
    private static ArdyRoomNavigation navigation;
    private static ArdyLocomotionAvatarReference reference;
    private static IEnumerator routine;
    private static Exception observationError;
    private static Transform[] bones;
    private static Vector3 userGround, userVelocity, previousRoot, previousLeftFoot, previousRightFoot, progressAnchor, stopRoot;
    private static Quaternion[] previousBones;
    private static float previousAt, progressAt, requestSpeed, stopAt, mainAt;
    private static int lastFrame;
    private static int previousStreamSteps;
    private static float previousAcknowledgedLastSample;
    private static bool previousConfirmedBoundKnown, previousReturning;
    private static ArdyLocomotionClip previousObservedClip;
    private static float lowHipsSince = -1, bothFeetBelowRestSince = -1;
    private static bool mainStarted;
    private static bool stopping, observe;
    private static GameObject obstacle;

    static ArdyRoomStreamRuntimeRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            { try { Initialize(); } catch (Exception error) { Finish(error); } }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) FinalizeBatch();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying)
                FinalizeBatch();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only run in isolated unity-naturalness-validation with -batchmode and without -quit.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play mode is already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a modified validation scene.");
            string asset = Argument("-ardyRoomAvatar", "Assets/Model/NeEEvA.vrm");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (prefab == null) throw new InvalidOperationException("Missing imported avatar: " + asset);
            var imported = ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), asset);
            SessionState.SetString(Key + "Avatar", asset);
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-room-stream-runtime")));
            SessionState.SetString(Key + "Endpoint", Argument("-ardyRoomEndpoint", "http://127.0.0.1:8093"));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = FloorName;
            floor.transform.position = new Vector3(0, -.1f, 0); floor.transform.localScale = new Vector3(20, .2f, 20);
            ArdyRoomGeometryPreparation.Prepare(floor.transform, false);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab); root.name = RootName;
            float scale = float.Parse(Argument("-ardyRoomAvatarScale", ".9"), CultureInfo.InvariantCulture);
            root.transform.localScale = Vector3.one * scale;
            root.transform.SetPositionAndRotation(new Vector3(0, -imported.groundY * scale, 0),
                Quaternion.Euler(0, float.Parse(Argument("-ardyRoomInitialYaw", "37"), CultureInfo.InvariantCulture), 0));
            root.GetComponent<Vrm10Instance>().enabled = false;
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            { a.enabled = false; a.runtimeAnimatorController = null; a.applyRootMotion = false; a.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 270);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion, avatar = SessionState.GetString(Key + "Avatar", ""),
            runtimeAssemblySha256 = Hash(typeof(ArdyLocomotionPlayer).Assembly.Location), harnessSha256 = Hash(typeof(ArdyRoomStreamRuntimeRegression).Assembly.Location) };
        avatar = GameObject.Find(RootName).GetComponent<Vrm10Instance>(); animator = avatar.GetComponent<Animator>();
        reference = ArdyLocomotionAvatarReference.FromImportedPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(report.avatar).GetComponent<Vrm10Instance>(), report.avatar);
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Check(animator.runtimeAnimatorController != null && !avatar.enabled, "Fixture needs the real direct-Humanoid Animator with VRM disabled.");
        animator.enabled = true; animator.SetInteger("state", 0);
        var host = new GameObject("ARDY stream validation host");
        navigation = host.AddComponent<ArdyRoomNavigation>();
        navigation.AgentHeight = Mathf.Max(1.1f, (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * avatar.transform.lossyScale.y + .18f);
        Check(navigation.Build(GameObject.Find(FloorName).transform, avatar.transform, out string error), error);
        controller = host.AddComponent<ArdyRoomLocomotionController>(); controller.Endpoint = SessionState.GetString(Key + "Endpoint", "");
        controller.Configure(avatar, reference, navigation);
        bones = Observed.Select(avatar.Humanoid.GetBoneTransform).Where(t => t != null).ToArray();
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        routine = Scenarios(); lastFrame = -1; observationError = null;
        InstallObserver(true);
    }

    private static IEnumerator Scenarios()
    {
        float until = Time.realtimeSinceStartup + .4f;
        while (Time.realtimeSinceStartup < until) yield return null;
        report.initialStandingHipsHeight = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips).position.y - Ground().y;
        float scale = avatar.transform.lossyScale.y;
        report.importedRestHipsHeight = (reference.Get(HumanBodyBones.Hips).positionInRoot.y - reference.groundY) * scale;
        report.importedRestLeftFootHeight = (reference.Get(HumanBodyBones.LeftFoot).positionInRoot.y - reference.groundY) * scale;
        report.importedRestRightFootHeight = (reference.Get(HumanBodyBones.RightFoot).positionInRoot.y - reference.groundY) * scale;
        Check(report.initialStandingHipsHeight > .3f && report.initialStandingHipsHeight > report.importedRestHipsHeight * .75f,
            "Initial Animator fixture is not standing upright enough to establish a walking-height baseline.");
        BeginCase("continuous-variable-speed-60s");
        userGround = new Vector3(0, 0, 3.5f); userVelocity = Vector3.right * .25f; requestSpeed = .65f;
        BeginStream();
        var wait = WaitForMotion(30); while (wait.MoveNext()) yield return wait.Current;
        current.session = controller.StreamSessionId; current.operation = controller.OperationRevision;
        current.initialSteps = controller.StreamStepCount; current.initialUnderruns = controller.StreamUnderrunCount;
        current.initialControl = controller.StreamControlRevision;
        mainAt = Time.realtimeSinceStartup; mainStarted = true; progressAt = mainAt; progressAnchor = avatar.transform.position;
        float last = mainAt, controlAt = mainAt, angle = 0;
        var speeds = new[] { .25f, .45f, .65f, 1f, .35f, .8f };
        while (Time.realtimeSinceStartup - mainAt < 60f)
        {
            float now = Time.realtimeSinceStartup, dt = now - last; last = now;
            float speed = speeds[Mathf.Clamp(Mathf.FloorToInt((now - mainAt) / 10f), 0, 5)];
            angle += speed * dt / 4.5f;
            userGround = new Vector3(4.5f * Mathf.Sin(angle), 0, -1f + 4.5f * Mathf.Cos(angle));
            userVelocity = new Vector3(Mathf.Cos(angle), 0, -Mathf.Sin(angle)) * speed;
            requestSpeed = Mathf.Clamp(speed + Mathf.Max(0, Horizontal(userGround - Ground()).magnitude - 1.25f) * .35f, .15f, 1.2f);
            if (now >= controlAt)
            {
                controlAt = now + .2f;
                var route = Plan();
                Check(controller.TryUpdateContinuousFollow(route, userGround, userGround + Vector3.up * 1.6f, userVelocity,
                    requestSpeed, true, out string error), "Continuous control update rejected: " + error);
            }
            Check(controller.IsContinuousFollowActive && controller.StreamSessionId == current.session && controller.OperationRevision == current.operation,
                "Continuous follow ended or restarted its session/operation during the main 60s interval.");
            Check(controller.StreamUnderrunCount == current.initialUnderruns,
                "The main interval ran out of generated motion; a cached stall cannot count as continuity.");
            Check(controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation,
                "The continuous interval released or returned the body instead of sustaining movement.");
            Check(now - progressAt < 2.5f, "Actual root made no useful progress for 2.5s while following a continuously moving user.");
            yield return null;
        }
        current.duration = Time.realtimeSinceStartup - mainAt;
        current.finalSteps = controller.StreamStepCount; current.finalControl = controller.StreamControlRevision;
        Check(current.duration >= 60 && current.rootTravel > 8f && current.finalSteps > current.initialSteps + 2 &&
            current.finalControl > current.initialControl + 100, "Main test did not span 60s of genuine fresh motion/control and meaningful root travel.");
        Check(current.maximumLeftKnee - current.minimumLeftKnee > 2f && current.maximumRightKnee - current.minimumRightKnee > 2f,
            "Root movement did not include measurable actual flexion in both legs; a frozen pose cannot pass as walking.");
        wait = StopAndObserve(); while (wait.MoveNext()) yield return wait.Current;
        mainStarted = false;
        EndCase();

        BeginCase("dynamic-obstacle-stops-before-contact");
        userVelocity = Horizontal(avatar.transform.forward).normalized * .6f; userGround = Ground() + userVelocity.normalized * 3.5f; requestSpeed = .6f;
        BeginStream(); wait = WaitForMotion(25); while (wait.MoveNext()) yield return wait.Current;
        Vector3 direction = Horizontal(userGround - Ground()).normalized;
        obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.name = "Isolated injected dynamic obstacle";
        obstacle.transform.SetPositionAndRotation(Ground() + direction * .85f + Vector3.up, Quaternion.LookRotation(direction));
        obstacle.transform.localScale = new Vector3(3, 2, .2f); Physics.SyncTransforms();
        var box = obstacle.GetComponent<BoxCollider>();
        until = Time.realtimeSinceStartup + 8f;
        while (controller.IsContinuousFollowActive || controller.Player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup < until, "Dynamic obstacle did not cause a bounded guarded stop.");
            Check(Vector3.Distance(box.ClosestPoint(Ground() + Vector3.up * .7f), Ground() + Vector3.up * .7f) > .05f,
                "The avatar root entered the injected wall before stopping.");
            yield return null;
        }
        current.sawExplicitFailure = controller.ExecutionState == ArdyRoomExecutionState.Blocked || controller.ExecutionState == ArdyRoomExecutionState.Failed;
        Check(current.sawExplicitFailure, "Obstacle exit was not reported as a guarded failure.");
        UnityEngine.Object.Destroy(obstacle); obstacle = null; yield return null; Physics.SyncTransforms();
        EndCase();

        BeginCase("unavailable-endpoint-cannot-pass-as-cached-stream");
        string endpoint = controller.Endpoint;
        userVelocity = -Horizontal(avatar.transform.forward).normalized * .5f; userGround = Ground() + userVelocity.normalized * 3.5f; requestSpeed = .5f;
        BeginStream(); wait = WaitForMotion(25); while (wait.MoveNext()) yield return wait.Current;
        int unavailableOperation = controller.OperationRevision;
        current.initialRecoveries = controller.StreamRecoveryCount;
        controller.Endpoint = "http://127.0.0.1:1";
        // A temporarily exhausted prefix may now preserve this operation through up to
        // three recoveries. HTTP refusal can fail immediately; neither path may loop
        // forever or keep playing an unconfirmed cached prediction.
        until = Time.realtimeSinceStartup + 45f;
        while (controller.IsContinuousFollowActive || controller.Player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup < until, "Unavailable endpoint did not produce a bounded stream failure after its buffer was exhausted.");
            Check(controller.OperationRevision == unavailableOperation && controller.StreamRecoveryCount - current.initialRecoveries <= 3,
                "Unavailable endpoint changed the follow operation or exceeded its bounded recovery budget.");
            yield return null;
        }
        current.finalRecoveries = controller.StreamRecoveryCount;
        current.sawExplicitFailure = controller.ExecutionState == ArdyRoomExecutionState.Blocked || controller.ExecutionState == ArdyRoomExecutionState.Failed;
        Check(current.sawExplicitFailure, "Network loss was hidden as normal continuous playback/completion.");
        controller.Endpoint = endpoint;
        Vector3 held = avatar.transform.position; long revision = controller.Player.PlaybackRevision;
        until = Time.realtimeSinceStartup + .8f;
        while (Time.realtimeSinceStartup < until)
        {
            Check(!controller.IsContinuousFollowActive && !controller.Player.HasPoseOwnership && controller.Player.PlaybackRevision == revision &&
                Vector3.Distance(held, avatar.transform.position) < .0001f, "Late stream result resumed after network failure.");
            yield return null;
        }
        EndCase();
    }

    private static void BeginStream()
    {
        Check(!controller.IsContinuousFollowActive && !controller.Player.HasPoseOwnership, "Previous scenario still owns motion.");
        controller.BeginContinuousFollow(Plan(), userGround, userGround + Vector3.up * 1.6f, userVelocity, requestSpeed, true);
    }
    private static ArdyRoomRoute Plan()
    {
        Vector3 offset = Horizontal(userGround - Ground());
        var target = userGround - (offset.sqrMagnitude > .0001f ? offset.normalized : Vector3.forward) * 1f;
        Check(navigation.TryPlan(Ground(), target, out var route, out string error), "Fixture route is not navigable: " + error);
        return route;
    }
    private static IEnumerator WaitForMotion(float timeout)
    {
        float deadline = Time.realtimeSinceStartup + timeout; Vector3 start = avatar.transform.position;
        while (controller.StreamStepCount < 1 || !controller.Player.HasPoseOwnership || Horizontal(avatar.transform.position - start).magnitude < .10f)
        {
            Check(Time.realtimeSinceStartup < deadline && controller.ExecutionState != ArdyRoomExecutionState.Failed &&
                controller.ExecutionState != ArdyRoomExecutionState.Blocked, "Stream failed to generate and begin actual root motion: " + controller.Status);
            yield return null;
        }
        Check(!string.IsNullOrEmpty(controller.StreamSessionId), "Motion started without a stream session identity.");
    }
    private static IEnumerator StopAndObserve()
    {
        stopRoot = avatar.transform.position; stopAt = Time.realtimeSinceStartup; stopping = true;
        var before = bones.Select(b => b.localRotation).ToArray();
        controller.Cancel();
        Check(!controller.IsContinuousFollowActive && !controller.Player.IsPlaying, "Explicit stop did not synchronously revoke stream/root playback.");
        for (int i = 0; i < bones.Length; i++) Check(Quaternion.Angle(before[i], bones[i].localRotation) < .1f, "Stop snapped the displayed body at request time.");
        int firstFrame = Time.frameCount;
        while (Time.frameCount - firstFrame < 25 || controller.Player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup - stopAt < 2f, "Stop did not finish its live Animator return.");
            Check(Vector3.Distance(stopRoot, avatar.transform.position) < .0001f, "Root moved after explicit stop.");
            yield return null;
        }
        current.stopFrames = Time.frameCount - firstFrame;
        Check(current.stopFrames >= 25 && current.sawPoseReturn && current.poseReleased && animator.enabled,
            "Stop was not observed for 25 frames through a real pose return and body release.");
        stopping = false;
    }

    private static void BeginCase(string name)
    {
        current = new Case { name = name, started = Time.realtimeSinceStartup }; report.cases.Add(current);
        previousBones = null; previousAt = 0; progressAt = current.started; progressAnchor = avatar.transform.position;
        previousConfirmedBoundKnown = previousReturning = false;
        previousObservedClip = null; previousStreamSteps = 0; previousAcknowledgedLastSample = 0;
        lowHipsSince = bothFeetBelowRestSince = -1;
        observe = true; stopping = false;
    }
    private static void EndCase()
    {
        current.status = "passed"; current.finalSteps = controller.StreamStepCount; current.finalControl = controller.StreamControlRevision;
        FinalizeMetrics(current);
        observe = false; Save(); current = null;
    }
    private static void FinalizeMetrics(Case value)
    {
        if (value.frames > 0)
        {
            value.meanSpeedError /= value.frames; value.meanFrameSeconds /= Mathf.Max(1, value.frames - 1);
            value.effectiveFps = value.meanFrameSeconds > 0 ? 1f / value.meanFrameSeconds : 0;
        }
    }
    private static void Observe()
    {
        if (!observe || current == null || controller == null || observationError != null) return;
        try
        {
            float now = Time.realtimeSinceStartup, dt = previousAt > 0 ? now - previousAt : 0;
            var root = avatar.transform.position; var rotations = bones.Select(b => b.localRotation).ToArray();
            Vector3 left = avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftFoot).position,
                right = avatar.Humanoid.GetBoneTransform(HumanBodyBones.RightFoot).position;
            float step = dt > 0 ? Horizontal(root - previousRoot).magnitude : 0, speed = dt > 0 ? step / dt : 0;
            float boneStep = 0;
            if (previousBones != null) for (int i = 0; i < bones.Length; i++) boneStep = Mathf.Max(boneStep, Quaternion.Angle(previousBones[i], rotations[i]));
            Check(Finite(root) && Finite(left) && Finite(right) && !float.IsNaN(speed) && !float.IsInfinity(speed), "Stream produced non-finite rendered coordinates.");
            Check(step < Mathf.Max(.15f, 2.5f * dt), "Actual root teleported during streamed locomotion.");
            Check(Mathf.Abs(Ground().y) < .035f, "Stream left the guarded floor height.");
            // The generated prediction is longer than its acknowledged prefix. A slow frame
            // must not sample that uncommitted future and only discover the underrun afterward.
            // Replaced/pruned clips have a new step number and therefore a different time basis.
            bool sameObservedClip = ReferenceEquals(controller.Player.Clip, previousObservedClip);
            if (previousConfirmedBoundKnown && sameObservedClip && controller.StreamStepCount == previousStreamSteps && !stopping)
                Check(controller.Player.TimeSeconds <= previousAcknowledgedLastSample + .002f,
                    "A rendered frame advanced beyond the previously acknowledged stream prefix before stopping.");
            if (previousReturning && controller.Player.IsReturningToAnimation && sameObservedClip)
                Check(Vector3.Distance(previousRoot, root) < .0001f,
                    "A recovery/stop handoff moved the frozen avatar root.");
            // Closing the wire intentionally reports zero future supply. It does not
            // subtract a source frame from the already confirmed clip being returned.
            // Read a fresh bound only from a live playing stream; retain its last known
            // bound through the same clip's return. A private restart prebuffer belongs
            // to a new timeline and must not be mixed with the old player's time basis.
            bool liveConfirmedBound = controller.IsContinuousFollowActive && controller.Player.IsPlaying &&
                !string.IsNullOrEmpty(controller.StreamSessionId);
            bool confirmedBoundKnown = liveConfirmedBound || sameObservedClip && previousConfirmedBoundKnown;
            float confirmedLastSample = liveConfirmedBound
                ? controller.Player.TimeSeconds + (float)controller.StreamBufferSeconds - .05f
                : confirmedBoundKnown ? previousAcknowledgedLastSample : 0;
            var hips = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
            float hipsHeight = hips.position.y - Ground().y, standingRatio = hipsHeight / report.initialStandingHipsHeight;
            float leftHeight = left.y - Ground().y, rightHeight = right.y - Ground().y;
            float leftClearance = leftHeight - report.importedRestLeftFootHeight,
                rightClearance = rightHeight - report.importedRestRightFootHeight;
            bool walking = controller.IsContinuousFollowActive && controller.Player.HasPoseOwnership &&
                !controller.Player.IsReturningToAnimation && !stopping;
            if (walking && standingRatio < report.lowHipsStandingFraction)
            { if (lowHipsSince < 0) lowHipsSince = now; }
            else lowHipsSince = -1;
            if (walking && leftClearance < -report.footBelowRestToleranceMetres && rightClearance < -report.footBelowRestToleranceMetres)
            { if (bothFeetBelowRestSince < 0) bothFeetBelowRestSince = now; }
            else bothFeetBelowRestSince = -1;
            float lowHipsRun = lowHipsSince >= 0 ? now - lowHipsSince : 0;
            float bothFeetRun = bothFeetBelowRestSince >= 0 ? now - bothFeetBelowRestSince : 0;
            current.minimumHipsHeight = Mathf.Min(current.minimumHipsHeight, hipsHeight);
            current.minimumHipsStandingRatio = Mathf.Min(current.minimumHipsStandingRatio, standingRatio);
            current.maximumLowHipsRunSeconds = Mathf.Max(current.maximumLowHipsRunSeconds, lowHipsRun);
            current.minimumLeftFootRestClearance = Mathf.Min(current.minimumLeftFootRestClearance, leftClearance);
            current.minimumRightFootRestClearance = Mathf.Min(current.minimumRightFootRestClearance, rightClearance);
            current.maximumBothFeetBelowRestRunSeconds = Mathf.Max(current.maximumBothFeetBelowRestRunSeconds, bothFeetRun);
            if (mainStarted && !stopping && current.name == "continuous-variable-speed-60s")
                AccumulateMainThird(now - mainAt, dt, hipsHeight, standingRatio, speed, requestSpeed, leftClearance, rightClearance);
            Quaternion calibratedHips = hips.rotation * Quaternion.Inverse(reference.Get(HumanBodyBones.Hips).rotationInRoot);
            float headingError = Vector3.Angle(Horizontal(avatar.transform.forward), Horizontal(calibratedHips * Vector3.forward));
            float travelFacing = speed > .08f ? Vector3.Angle(Horizontal(avatar.transform.forward), Horizontal(root - previousRoot)) : 0;
            float leftSpeed = dt > 0 ? Horizontal(left - previousLeftFoot).magnitude / dt : 0,
                rightSpeed = dt > 0 ? Horizontal(right - previousRightFoot).magnitude / dt : 0;
            float leftKnee = Knee(true), rightKnee = Knee(false);
            current.frames++; current.rootTravel += step;
            current.maximumFrameSeconds = Mathf.Max(current.maximumFrameSeconds, dt); current.meanFrameSeconds += dt;
            current.minimumLeftKnee = Mathf.Min(current.minimumLeftKnee, leftKnee); current.maximumLeftKnee = Mathf.Max(current.maximumLeftKnee, leftKnee);
            current.minimumRightKnee = Mathf.Min(current.minimumRightKnee, rightKnee); current.maximumRightKnee = Mathf.Max(current.maximumRightKnee, rightKnee);
            current.maximumRootStep = Mathf.Max(current.maximumRootStep, step); current.maximumBoneStep = Mathf.Max(current.maximumBoneStep, boneStep);
            current.maximumFootSpeed = Mathf.Max(current.maximumFootSpeed, Mathf.Max(leftSpeed, rightSpeed));
            current.maximumRootBodyHeadingError = Mathf.Max(current.maximumRootBodyHeadingError, headingError);
            current.maximumTravelFacingError = Mathf.Max(current.maximumTravelFacingError, travelFacing);
            current.maximumUnderruns = Mathf.Max(current.maximumUnderruns, controller.StreamUnderrunCount);
            float error = Mathf.Abs(speed - requestSpeed); current.maximumSpeedError = Mathf.Max(current.maximumSpeedError, error); current.meanSpeedError += error;
            if (Horizontal(root - progressAnchor).magnitude >= .03f)
            { current.maximumProgressGap = Mathf.Max(current.maximumProgressGap, now - progressAt); progressAt = now; progressAnchor = root; }
            if (stopping)
            {
                current.stopRootDrift = Mathf.Max(current.stopRootDrift, Vector3.Distance(stopRoot, root));
                if (controller.Player.IsReturningToAnimation)
                { if (!current.sawPoseReturn) current.firstStopBoneStep = boneStep; current.sawPoseReturn = true; }
                current.maximumStopBoneStep = Mathf.Max(current.maximumStopBoneStep, boneStep);
                if (!controller.Player.HasPoseOwnership && !current.poseReleased)
                { current.poseReleased = true; current.returnedAfterSeconds = now - stopAt; }
            }
            Check(report.samples.Count < 18000, "Bounded observer exceeded its sample budget.");
            report.samples.Add(new Sample { frame = Time.frameCount, realtime = now, unityTime = Time.time,
                frameDeltaTime = dt, engineDeltaTime = Time.deltaTime, caseTime = now - current.started, phase = current.name,
                state = controller.ExecutionState.ToString(), stage = controller.Stage, session = controller.StreamSessionId,
                operation = controller.OperationRevision, steps = controller.StreamStepCount, control = controller.StreamControlRevision,
                acceptedControl = controller.StreamAcceptedControlRevision, appliedControl = controller.StreamAppliedControlRevision,
                controlLatencySeconds = controller.StreamControlLatencySeconds, active = controller.IsContinuousFollowActive,
                playing = controller.Player.IsPlaying, owned = controller.Player.HasPoseOwnership, returning = controller.Player.IsReturningToAnimation,
                desiredSpeed = (float)controller.StreamDesiredSpeed, measuredSpeed = (float)controller.StreamMeasuredSpeed, actualSpeed = speed,
                bodySampleTime = controller.Player.TimeSeconds, bufferSeconds = (float)controller.StreamBufferSeconds,
                acknowledgedBoundKnown = confirmedBoundKnown, acknowledgedLastSampleSeconds = confirmedLastSample,
                generationSeconds = (float)controller.StreamGenerationSeconds, underruns = controller.StreamUnderrunCount,
                root = root, userGround = userGround, userVelocity = userVelocity, userDistance = Horizontal(userGround - Ground()).magnitude,
                hips = hips.position, leftFoot = left, rightFoot = right, leftFootSpeed = leftSpeed, rightFootSpeed = rightSpeed,
                maximumBoneStep = boneStep, headingDegrees = avatar.transform.eulerAngles.y, bodyHeadingDegrees = calibratedHips.eulerAngles.y,
                rootBodyHeadingError = headingError, travelFacingError = travelFacing,
                predictedLeftContact = controller.Player.Clip != null && controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation
                    ? controller.Player.Clip.SampleContact(true, controller.Player.TimeSeconds) : -1,
                predictedRightContact = controller.Player.Clip != null && controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation
                    ? controller.Player.Clip.SampleContact(false, controller.Player.TimeSeconds) : -1,
                hipsHeightAboveGround = hipsHeight, hipsStandingRatio = standingRatio,
                leftFootHeightAboveGround = leftHeight, rightFootHeightAboveGround = rightHeight,
                leftFootRestClearance = leftClearance, rightFootRestClearance = rightClearance,
                lowHipsRunSeconds = lowHipsRun, bothFeetBelowRestRunSeconds = bothFeetRun,
                leftKneeDegrees = leftKnee, rightKneeDegrees = rightKnee });
            previousAt = now; previousRoot = root; previousBones = rotations; previousLeftFoot = left; previousRightFoot = right;
            previousConfirmedBoundKnown = confirmedBoundKnown;
            previousReturning = controller.Player.IsReturningToAnimation;
            previousObservedClip = controller.Player.Clip;
            previousStreamSteps = controller.StreamStepCount;
            previousAcknowledgedLastSample = confirmedLastSample;
            // Append the failing rendered frame before raising, so the pose and unchanged
            // native wire evidence explain a quality failure rather than only a generic stop.
            Check(lowHipsRun <= report.lowHipsMaximumContinuousSeconds,
                $"Walking hips collapsed to {standingRatio:P1} of initial standing height for {lowHipsRun:F3}s ({hipsHeight:F3}m / {report.initialStandingHipsHeight:F3}m).");
            Check(bothFeetRun <= report.bothFeetBelowRestMaximumContinuousSeconds,
                $"Both foot landmarks stayed more than {report.footBelowRestToleranceMetres:F2}m below their neutral floor-relative height for {bothFeetRun:F3}s.");
        }
        catch (Exception error) { observationError = error; }
    }

    private static void AccumulateMainThird(float seconds, float dt, float hips, float ratio, float speed, float desired,
        float leftClearance, float rightClearance)
    {
        if (seconds < 0 || seconds >= 60) return;
        var part = report.mainThirds[Mathf.Clamp(Mathf.FloorToInt(seconds / 20f), 0, 2)];
        float weight = Mathf.Max(0, Mathf.Min(dt, seconds - part.fromSeconds));
        if (!part.observed)
        {
            part.observed = true; part.minimumHipsHeight = hips;
            part.minimumLeftFootRestClearance = leftClearance; part.minimumRightFootRestClearance = rightClearance;
        }
        part.samples++; part.observedSeconds += weight;
        part.hipsSum += hips * weight; part.ratioSum += ratio * weight;
        part.speedSum += speed * weight; part.desiredSum += desired * weight;
        part.minimumHipsHeight = Mathf.Min(part.minimumHipsHeight, hips); part.maximumHipsHeight = Mathf.Max(part.maximumHipsHeight, hips);
        part.minimumLeftFootRestClearance = Mathf.Min(part.minimumLeftFootRestClearance, leftClearance);
        part.minimumRightFootRestClearance = Mathf.Min(part.minimumRightFootRestClearance, rightClearance);
        if (part.observedSeconds > 0)
        {
            part.meanHipsHeight = part.hipsSum / part.observedSeconds; part.meanStandingRatio = part.ratioSum / part.observedSeconds;
            part.meanActualSpeed = part.speedSum / part.observedSeconds; part.meanDesiredSpeed = part.desiredSum / part.observedSeconds;
        }
    }

    private static float Knee(bool left)
    {
        var thigh = avatar.Humanoid.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
        var knee = avatar.Humanoid.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
        var foot = avatar.Humanoid.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
        return Vector3.Angle(thigh.position - knee.position, foot.position - knee.position);
    }
    private static Vector3 Ground() => avatar.transform.position + Vector3.up * reference.groundY * avatar.transform.lossyScale.y;
    private static Vector3 Horizontal(Vector3 v) { v.y = 0; return v; }
    private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Continuous-follow validation exceeded its 270s bound.")); return; }
        if (!EditorApplication.isPlaying || report == null || routine == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try { if (observationError != null) throw observationError; if (!routine.MoveNext()) Finish(null); }
        catch (Exception error) { Finish(error); }
    }
    private static void InstallObserver(bool install)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop(); Rewrite(ref loop); PlayerLoop.SetPlayerLoop(loop);
        void Rewrite(ref PlayerLoopSystem item)
        {
            if (item.subSystemList == null) return;
            var children = item.subSystemList.Where(c => c.type != typeof(ArdyRoomStreamRuntimeRegression)).ToList();
            for (int i = 0; i < children.Count; i++) { var child = children[i]; Rewrite(ref child); children[i] = child; }
            if (install && item.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                children.Add(new PlayerLoopSystem { type = typeof(ArdyRoomStreamRuntimeRegression), updateDelegate = Observe });
            item.subSystemList = children.ToArray();
        }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        SessionState.SetBool(Key + "Finished", true); InstallObserver(false); observe = false;
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        if (current != null && error != null) { current.status = "failed"; current.error = error.ToString(); FinalizeMetrics(current); }
        // Teardown calls Cancel, which changes a still-generating record to cancelled and
        // clears the stream identity. Preserve the failure-time evidence before that mutation.
        string finalWire = CaptureWire();
        try { (routine as IDisposable)?.Dispose(); controller?.Cancel(); if (controller?.Player != null) controller.Player.Stop(); }
        catch (Exception cleanup) { report.status = "failed"; report.error += "\nCleanup: " + cleanup; }
        if (obstacle != null) UnityEngine.Object.Destroy(obstacle);
        Save(finalWire); SessionState.SetInt(Key + "Exit", report.status == "passed" ? 0 : 1);
        if (error != null) Debug.LogException(error);
        EditorApplication.isPlaying = false;
    }
    private static void FinalizeBatch()
    {
        int code = SessionState.GetInt(Key + "Exit", 1); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(code);
    }
    private static void Save(string wire = null)
    {
        string output = SessionState.GetString(Key + "Output", "Logs/ardy-room-stream-runtime");
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "stream-runtime.json"), JsonUtility.ToJson(report, true));
        wire = wire ?? CaptureWire();
        if (wire == null) return;
        File.WriteAllText(Path.Combine(output, "stream-wire.json"), wire);
        // Keep earlier completed-case evidence when a later network/obstacle case overwrites
        // the latest snapshot. Checkpoints run after stopping, never inject JSON IO into gait.
        if (current != null) File.WriteAllText(Path.Combine(output, current.name + "-stream-wire.json"), wire);
    }
    private static string CaptureWire()
    {
        if (controller == null) return null;
        var segments = controller.Segments;
        var retained = segments.Skip(Mathf.Max(0, segments.Count - 64)).ToArray();
        return JsonUtility.ToJson(new WireSnapshot {
            caseName = current?.name ?? "final", status = controller.ExecutionState.ToString(), stage = controller.Stage,
            reason = controller.Status, session = controller.StreamSessionId,
            capturedAtRealtime = Time.realtimeSinceStartup, operation = controller.OperationRevision, revision = controller.Revision,
            stepCount = controller.StreamStepCount, controlRevision = controller.StreamControlRevision,
            acceptedControlRevision = controller.StreamAcceptedControlRevision,
            appliedControlRevision = controller.StreamAppliedControlRevision, underruns = controller.StreamUnderrunCount,
            bufferSeconds = controller.StreamBufferSeconds, generationSeconds = controller.StreamGenerationSeconds,
            active = controller.IsContinuousFollowActive, busy = controller.IsBusy,
            containsInFlightRequest = retained.Any(s => s.outcome == "generating"),
            discardedByController = controller.DiscardedSegmentEvidenceCount, omittedBySnapshot = Mathf.Max(0, segments.Count - 64),
            lastRequestJson = controller.LastRequestJson, lastResponseJson = controller.LastResponseJson,
            lastErrorResponseJson = controller.LastErrorResponseJson, segments = retained
        }, true);
    }
    private static void Check(bool value, string reason) { if (report != null) report.checks++; if (!value) throw new InvalidOperationException(reason); }
    private static string Argument(string name, string fallback)
    { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Hash(string path)
    { using (var file = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
}
