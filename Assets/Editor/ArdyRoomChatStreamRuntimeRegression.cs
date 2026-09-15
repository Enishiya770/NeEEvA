using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.UI;

/// <summary>Short, real-HTTP Chat follow/wait/resume/stop integration in a disposable batch scene.</summary>
[InitializeOnLoad]
public static class ArdyRoomChatStreamRuntimeRegression
{
    private const string Key = "NeEEvA.ARDY.ChatStreamRuntime.";
    private const string AvatarName = "ARDY Chat Stream Avatar", FloorName = "ARDY Chat Stream Floor";
    private const string ChatName = "ARDY Chat Stream Chat";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Serializable] private sealed class Source { public string path, sha256; }
    [Serializable] private sealed class Sample
    {
        public int frame, operation, streamSteps, streamControl, appliedControl, underruns;
        public long liveRevision, playbackRevision;
        public string phase, stage, task, session, observationPhase;
        public bool following, continuous, bodyReserved, poseOwned, playing, returning, animatorEnabled, liveEnabled;
        public float realtime, userDistance, estimatedUserSpeed, requestedSpeed, measuredSpeed, bufferSeconds;
        public Vector3 root, user;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, avatar, assemblySha256, harnessSha256;
        public string scope = "Actual ChatSample completed-motion parsing and dispatch, scene bridge, user-speed policy, real ARDY streaming HTTP and project Animator. One bounded follow task moves, waits, resumes over an actual nod, and is explicitly stopped.";
        public string limitations = "No live ASR/TTS/Qwen generation, private user scene, subjective naturalness or long-duration stress claim. The explicit stop uses the production task/operation identity API. Live cancel count excludes the separately observed full-body owner's OnDisable cancellation.";
        public int checks, failureIdentityChecks, ownershipChecks, operation, stoppedOperation, initialSteps, updatedSteps, resumedSteps, initialControl,
            updatedControl, resumedControl, resumeOwnerDisableCount, resumeExplicitCancellationCount;
        public long resumeLiveRevisionBefore, resumeLiveRevisionAfter;
        public bool chatServicesDormant, realChatFollowDispatched, sameTaskAtWait, sameTaskAfterResume,
            animatorReleasedAtWait, actualNodStarted, resumeCancelledNodOnce, explicitStopRevoked,
            stopPreventedRestart, ordinarySpeechPreservedFollow, waitingForeignOwnerPreserved;
        public string task, firstSession, resumedSession, resumeEndReason, finalFacts;
        public float duration, firstUserTravel, firstRootTravel, firstEstimatedSpeed, pauseWaitSeconds,
            pauseDistance, resumeSeconds, resumeUserTravel, resumeRootTravel, stopRootDrift, maximumRootStep;
        public List<Source> sources = new List<Source>();
        public List<Sample> samples = new List<Sample>();
    }

    private static Report report;
    private static ChatSample chat;
    private static ArdyRoomDialogueBridge bridge;
    private static ArdyLiveMotionController live;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static Transform user;
    private static FieldInfo vrmRuntime;
    private static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
    private static Exception observationError;
    private static int lastFrame;
    private static string observationPhase;
    private static float started, scenarioDeadline, previousSampleAt;
    private static Vector3 previousRoot;
    private static bool observe, countResumeCancel, previousLiveEnabled;

    static ArdyRoomChatStreamRuntimeRegression()
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

    public static void RunBatch() => StartBatch(false);
    public static void RunLifecycleBatch() => StartBatch(true);

    private static void StartBatch(bool lifecycleOnly)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only run in isolated unity-naturalness-validation with -batchmode and without -quit.");
        try
        {
            SessionState.SetBool(Key + "LifecycleOnly", lifecycleOnly);
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play mode already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a modified validation scene.");
            string asset = Argument("-ardyRoomAvatar", "Assets/Model/NeEEvA.vrm");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (prefab == null) throw new InvalidOperationException("Missing imported avatar: " + asset);
            var imported = ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), asset);
            SessionState.SetString(Key + "Avatar", asset);
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-room-chat-stream-runtime")));
            SessionState.SetString(Key + "Endpoint", Argument("-ardyRoomEndpoint", "http://127.0.0.1:8093"));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = FloorName;
            floor.transform.position = new Vector3(0, -.1f, 0); floor.transform.localScale = new Vector3(12, .2f, 12);
            ArdyRoomGeometryPreparation.Prepare(floor.transform, false);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab); root.name = AvatarName;
            float scale = float.Parse(Argument("-ardyRoomAvatarScale", ".9"), CultureInfo.InvariantCulture);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0) throw new ArgumentException("Invalid scale.");
            root.transform.localScale = Vector3.one * scale;
            root.transform.SetPositionAndRotation(new Vector3(0, -imported.groundY * scale, 0), Quaternion.identity);
            root.GetComponent<Vrm10Instance>().enabled = false;
            foreach (var owner in root.GetComponentsInChildren<Animator>(true))
            { owner.enabled = false; owner.runtimeAnimatorController = null; owner.applyRootMotion = false; owner.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            // Actual disabled ChatSample parser/event bus, with ordinary Awake UI references.
            // Its service-starting Start and speech loop never run in this fixture.
            var host = new GameObject(ChatName); host.SetActive(false);
            var sample = host.AddComponent<ChatSample>(); sample.enabled = false;
            var button = new GameObject("Fixture commit", typeof(RectTransform), typeof(Button)).GetComponent<Button>();
            var text = new GameObject("Fixture text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            button.transform.SetParent(host.transform, false); text.transform.SetParent(host.transform, false);
            SetField(sample, "m_CommitMsgBtn", button); SetField(sample, "m_TextBack", text);
            SetField(sample, "m_Animator", root.GetComponent<Animator>());
            SetField(sample, "m_PersistSubtitleSettings", false); SetField(sample, "m_EnableSubtitleTranslation", false);
            host.AddComponent<ArdyRoomDialogueBridge>().connectOnStart = false;
            host.SetActive(true);
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 90f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion, avatar = SessionState.GetString(Key + "Avatar", ""),
            assemblySha256 = Hash(typeof(ArdyRoomDialogueBridge).Assembly.Location), harnessSha256 = Hash(typeof(ArdyRoomChatStreamRuntimeRegression).Assembly.Location) };
        if (SessionState.GetBool(Key + "LifecycleOnly", false))
        {
            report.scope = "Controlled waiting-resume lifecycle: real Chat follow and cached nod, actual continuous-controller capabilities request and zero-speed waiting, then the production waiting-resume helper cancels nod before a moving-user/zero-speed command would be dispatched. A temporary recovery flag/history stage checks that this helper cannot acquire a gesture during recovery. Typed stop revokes the actual waiting task.";
            report.limitations = "No native motion-generation request, initial walking, full-body resumption, dynamic following quality or end-to-end follow success claim. The zero-speed leg is explicitly submitted through the production bridge leg method to establish waiting without depending on a stochastic initial walk. Recovery flags are controlled observations; the actual bounded stream-recovery runtime is tested separately.";
        }
        foreach (string path in new[] { report.avatar, AnimatorPath,
            "Assets/AIChatTookit/Scripts/Chat/ArdyRoomDialogueBridge.cs", "Assets/AIChatTookit/Scripts/Chat/ArdyRoomFollowMotionPolicy.cs",
            "Assets/AIChatTookit/Scripts/Chat/ChatSample.Motion.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.Stream.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.Stream.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomStreamTrajectory.cs",
            "Assets/Editor/ArdyRoomChatStreamRuntimeRegression.cs", "Assets/Editor/ArdyRoomFollowFailureIdentityRegression.cs" }) report.sources.Add(new Source { path = path, sha256 = Hash(path) });
        avatar = GameObject.Find(AvatarName).GetComponent<Vrm10Instance>(); animator = avatar.GetComponent<Animator>();
        vrmRuntime = typeof(Vrm10Instance).GetField("m_runtime", Private);
        Check(vrmRuntime != null && !avatar.enabled && vrmRuntime.GetValue(avatar) == null, "Fixture initialized the disabled VRM runtime.");
        var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(report.avatar).GetComponent<Vrm10Instance>(), report.avatar);
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Check(animator.runtimeAnimatorController != null, "Project idle Animator is missing.");
        animator.enabled = true; animator.SetInteger("state", 0);
        chat = GameObject.Find(ChatName).GetComponent<ChatSample>(); bridge = chat.GetComponent<ArdyRoomDialogueBridge>();
        user = new GameObject("Explicit Chat stream user head").transform; user.position = new Vector3(0, 1.6f, 2.6f);
        bridge.chat = chat; bridge.avatar = avatar; bridge.importedReference = reference; bridge.userHead = user;
        bridge.environmentRoot = GameObject.Find(FloorName).transform; bridge.serviceUrl = SessionState.GetString(Key + "Endpoint", "");
        bridge.Connect(); live = avatar.GetComponent<ArdyLiveMotionController>();
        Check(bridge.IsConnected && chat.RoomMotionOutputEnabled && live != null && live.IsBound, "Actual runtime bridge failed to connect.");
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        started = Time.realtimeSinceStartup; scenarioDeadline = started + 30f;
        observationError = null; observe = true; observationPhase = "warm-idle";
        previousSampleAt = 0; lastFrame = -1; countResumeCancel = false;
        routines.Clear(); routines.Push(SessionState.GetBool(Key + "LifecycleOnly", false) ? LifecycleScenario() : Scenario()); InstallObserver(true);
    }

    private static IEnumerator Scenario()
    {
        yield return Delay(.85f);
        RunDormantIdentityChecks();
        observationPhase = "initial-follow";
        var intent = RoleCommand("follow");
        // Dispatch stamps its value-type intent with the Chat operation identity. The
        // parser's earlier copy is unstamped; read the accepted identity from the bridge.
        report.task = bridge.ActiveTaskId; report.operation = bridge.Controller.OperationRevision;
        Check(!string.IsNullOrEmpty(report.task) && bridge.IsFollowing && bridge.Controller.IsContinuousFollowActive,
            "Real Chat follow command did not start one continuous task.");
        report.realChatFollowDispatched = true;
        Vector3 startRoot = avatar.transform.position;
        yield return WaitFor(() => bridge.Controller.Player.IsPlaying && Horizontal(avatar.transform.position - startRoot).magnitude >= .15f,
            8f, "actual initial walking");
        report.firstSession = bridge.Controller.StreamSessionId;
        report.initialSteps = bridge.Controller.StreamStepCount; report.initialControl = bridge.Controller.StreamControlRevision;
        Check(!string.IsNullOrEmpty(report.firstSession), "Walking has no real native stream session.");
        // Exercise the production speech-cancellation event while the full body is reserved.
        Invoke(chat, "CancelDialogueMotion", new object[] { "user-started-speaking" });
        AssertSameTask(); report.ordinarySpeechPreservedFollow = true;
        observationPhase = "moving-user";
        Vector3 userStart = user.position; startRoot = avatar.transform.position;
        yield return MoveUser(2f, .35f, true);
        report.firstUserTravel = Horizontal(user.position - userStart).magnitude;
        report.firstRootTravel = Horizontal(avatar.transform.position - startRoot).magnitude;
        report.firstEstimatedSpeed = bridge.ReadCurrentTargetEvidence().followEstimatedUserSpeed;
        report.updatedSteps = bridge.Controller.StreamStepCount; report.updatedControl = bridge.Controller.StreamControlRevision;
        Check(report.firstUserTravel > .65f && report.firstRootTravel > .20f && report.firstEstimatedSpeed > .2f &&
            report.updatedSteps > report.initialSteps && report.updatedControl >= report.initialControl + 10,
            "Moving user did not produce observed velocity, fresh native steps, control updates and actual root progress.");
        observationPhase = "user-paused";
        float pauseAt = Time.realtimeSinceStartup;
        yield return WaitFor(() => bridge.Controller.Stage == "stream-waiting" && !bridge.Controller.Player.HasPoseOwnership,
            10f, "near-user idle handoff");
        report.pauseWaitSeconds = Time.realtimeSinceStartup - pauseAt;
        AssertSameTask(); report.sameTaskAtWait = true;
        report.pauseDistance = Horizontal(user.position - avatar.transform.position).magnitude;
        Check(report.pauseDistance <= bridge.approachDistance + .4f && !bridge.ReservesBody && animator.enabled,
            "Near-user wait did not reach interaction distance and release the actual Animator.");
        report.animatorReleasedAtWait = true;
        observationPhase = "waiting-nod";
        RoleCommand("nod");
        var upper = avatar.GetComponent<ArdyMotionPlayer>();
        Check(upper != null && upper.IsPlaying && live.DescribeActiveMotion().Contains("nod"),
            "The waiting follow prevented an actual Chat nod from starting.");
        report.actualNodStarted = true;
        yield return null;
        AssertSameTask();
        report.resumeLiveRevisionBefore = live.Revision;
        previousLiveEnabled = live.isActiveAndEnabled; countResumeCancel = true;
        observationPhase = "resuming-user";
        float resumeAt = Time.realtimeSinceStartup; userStart = user.position; startRoot = avatar.transform.position;
        yield return MoveUser(2.5f, .6f, false);
        yield return WaitFor(() => bridge.Controller.Player.IsPlaying && Horizontal(avatar.transform.position - startRoot).magnitude >= .15f,
            5f, "actual resumed walking");
        report.resumeSeconds = Time.realtimeSinceStartup - resumeAt;
        report.resumeUserTravel = Horizontal(user.position - userStart).magnitude;
        report.resumeRootTravel = Horizontal(avatar.transform.position - startRoot).magnitude;
        AssertSameTask(); report.sameTaskAfterResume = true;
        report.resumedSession = bridge.Controller.StreamSessionId;
        report.resumedSteps = bridge.Controller.StreamStepCount; report.resumedControl = bridge.Controller.StreamControlRevision;
        Check(!string.IsNullOrEmpty(report.resumedSession) && report.resumedSession != report.firstSession &&
            report.resumedSteps > report.updatedSteps && report.resumedControl > report.updatedControl,
            "Resume reused a closed session or failed to generate/control fresh native motion.");
        // A producer disable also calls Cancel, legitimately. Count it separately from the
        // bridge's explicit resume cancellation, using the actual enabled-state transition.
        report.resumeLiveRevisionAfter = live.Revision;
        report.resumeExplicitCancellationCount = checked((int)(report.resumeLiveRevisionAfter - report.resumeLiveRevisionBefore)) - report.resumeOwnerDisableCount;
        // Public live context is intentionally unavailable while the full-body player has
        // disabled that producer. Read its retained lifecycle reason, without mutating it.
        report.resumeEndReason = (string)(typeof(ArdyLiveMotionController).GetField("lastMotionEndReason", Private)
            ?? throw new MissingFieldException(nameof(ArdyLiveMotionController), "lastMotionEndReason")).GetValue(live);
        Check(report.resumeOwnerDisableCount == 1 && report.resumeExplicitCancellationCount == 1 &&
            report.resumeEndReason == "room-follow-resumed", "Resume did not cancel the existing nod exactly once before the full-body owner disabled its producer.");
        report.resumeCancelledNodOnce = true; countResumeCancel = false;
        observationPhase = "explicit-stop";
        Vector3 stoppedRoot = avatar.transform.position;
        Check(bridge.StopForTaskIfCurrent("chat-stream-explicit-stop", report.task, report.operation, out string failure, true),
            "Matching explicit stop was rejected: " + failure);
        report.stoppedOperation = bridge.Controller.OperationRevision;
        Check(!bridge.IsFollowing && !bridge.Controller.IsContinuousFollowActive && !bridge.Controller.Player.IsPlaying,
            "Explicit stop did not synchronously revoke follow and native root playback.");
        report.explicitStopRevoked = true;
        long stoppedPlayback = bridge.Controller.Player.PlaybackRevision;
        int stoppedSteps = bridge.Controller.StreamStepCount;
        float stopAt = Time.realtimeSinceStartup, last = stopAt;
        while (Time.realtimeSinceStartup - stopAt < 2f)
        {
            float now = Time.realtimeSinceStartup; user.position += Vector3.forward * Mathf.Min(now - last, .1f) * .25f; last = now;
            report.stopRootDrift = Mathf.Max(report.stopRootDrift, Vector3.Distance(stoppedRoot, avatar.transform.position));
            Check(!bridge.IsFollowing && !bridge.Controller.IsContinuousFollowActive && bridge.Controller.OperationRevision == report.stoppedOperation &&
                bridge.Controller.StreamStepCount == stoppedSteps && bridge.Controller.Player.PlaybackRevision == stoppedPlayback &&
                report.stopRootDrift < .0001f, "A user update or late native response restarted explicitly stopped follow.");
            yield return null;
        }
        Check(!bridge.Controller.Player.HasPoseOwnership && !bridge.ReservesBody && animator.enabled,
            "Explicit stop did not finish returning the body to the live Animator.");
        report.stopPreventedRestart = true;
        observationPhase = "waiting-foreign-owner";
        yield return ArdyRoomStreamOwnershipRegression.RunRuntimeChecks(bridge.Controller, bridge.importedReference,
            bridge.Controller.LastClip, Output());
        Check(ArdyRoomStreamOwnershipRegression.Completed, "Waiting player ownership regression did not complete.");
        report.ownershipChecks = ArdyRoomStreamOwnershipRegression.Checks;
        report.waitingForeignOwnerPreserved = true;
    }

    private static void RunDormantIdentityChecks()
    {
        observationPhase = "failure-identity-checks";
        int beforeRevision = bridge.Controller.Revision, beforeOperation = bridge.Controller.OperationRevision;
        Vector3 beforeRoot = avatar.transform.position, beforeUser = user.position;
        string beforePhase = bridge.Phase;
        report.failureIdentityChecks = ArdyRoomFollowFailureIdentityRegression.RunChecks(bridge);
        Check(report.failureIdentityChecks > 0 && !bridge.IsFollowing && !bridge.ReservesBody &&
            !bridge.Controller.IsContinuousFollowActive && !bridge.Controller.IsBusy && !bridge.Controller.Player.HasPoseOwnership &&
            bridge.Controller.Revision == beforeRevision && bridge.Controller.OperationRevision == beforeOperation &&
            bridge.Phase == beforePhase && avatar.transform.position == beforeRoot && user.position == beforeUser,
            "Synchronous controller-event checks did not restore the dormant fixture before real Chat follow.");
    }

    private static IEnumerator LifecycleScenario()
    {
        yield return Delay(.85f);
        RunDormantIdentityChecks();
        observationPhase = "controlled-native-wait";
        Vector3 heldRoot = avatar.transform.position;
        user.position = new Vector3(heldRoot.x, 1.6f, heldRoot.z + 1f);
        RoleCommand("follow");
        report.task = bridge.ActiveTaskId;
        Check(bridge.IsFollowing && !bridge.ReservesBody && !string.IsNullOrEmpty(report.task),
            "Real Chat follow did not retain a nearby waiting task.");
        // Submit the production zero-distance leg explicitly: this makes the same
        // continuous session reach waiting through real capabilities validation.
        Invoke(bridge, "BeginApproachLeg", new object[] { report.task });
        report.operation = bridge.Controller.OperationRevision;
        yield return WaitFor(() => bridge.Controller.Stage == "stream-waiting" && !bridge.Controller.Player.HasPoseOwnership,
            5f, "real zero-speed controller waiting");
        AssertSameTask();
        Check(bridge.Controller.StreamStepCount == 0 && string.IsNullOrEmpty(bridge.Controller.LastRequestJson) &&
            string.IsNullOrEmpty(bridge.Controller.StreamSessionId), "Lifecycle setup unexpectedly requested generated walking.");
        report.realChatFollowDispatched = report.sameTaskAtWait = report.animatorReleasedAtWait = true;

        observationPhase = "controlled-real-nod";
        RoleCommand("nod");
        var upper = avatar.GetComponent<ArdyMotionPlayer>();
        Check(upper != null && upper.IsPlaying && live.DescribeActiveMotion().Contains("nod"),
            "The real waiting Chat nod did not acquire upper-body playback.");
        report.actualNodStarted = true;
        yield return Delay(.1f);
        report.resumeLiveRevisionBefore = live.Revision;
        Invoke(bridge, "PrepareWaitingFollowResume", new object[] { false, 0f });
        Check(live.Revision == report.resumeLiveRevisionBefore && upper.IsPlaying,
            "Stationary waiting cancelled the real nod.");

        // Recovery may be collecting history while the stage label changes. An
        // explicit observed recovery flag must still prohibit acquiring this nod.
        string savedStage = bridge.Controller.Stage;
        SetField(bridge.Controller, "streamRecovering", true);
        typeof(ArdyRoomLocomotionController).GetProperty("Stage").SetValue(bridge.Controller, "stream-history");
        try
        {
            Invoke(bridge, "PrepareWaitingFollowResume", new object[] { true, 0f });
            Check(live.Revision == report.resumeLiveRevisionBefore && upper.IsPlaying,
                "Recovery history preparation stole the real gesture owner.");
        }
        finally
        {
            SetField(bridge.Controller, "streamRecovering", false);
            typeof(ArdyRoomLocomotionController).GetProperty("Stage").SetValue(bridge.Controller, savedStage);
        }

        observationPhase = "controlled-pre-dispatch-cancel";
        Invoke(bridge, "PrepareWaitingFollowResume", new object[] { true, 0f });
        report.resumeLiveRevisionAfter = live.Revision;
        report.resumeExplicitCancellationCount = checked((int)(report.resumeLiveRevisionAfter - report.resumeLiveRevisionBefore));
        report.resumeEndReason = (string)typeof(ArdyLiveMotionController).GetField("lastMotionEndReason", Private).GetValue(live);
        Check(report.resumeExplicitCancellationCount == 1 && report.resumeEndReason == "room-follow-resumed" &&
            upper.MotionPlaybackWasInterrupted && live.isActiveAndEnabled && !bridge.Controller.Player.HasPoseOwnership,
            "Moving-user/zero-speed wakeup did not cancel the actual nod exactly once before full-body acquisition.");
        Invoke(bridge, "PrepareWaitingFollowResume", new object[] { true, 0f });
        Check(live.Revision == report.resumeLiveRevisionAfter && bridge.Controller.StreamStepCount == 0 &&
            string.IsNullOrEmpty(bridge.Controller.LastRequestJson),
            "Duplicate pre-dispatch resume cancelled again or dispatched unrequested native walking.");
        report.resumeCancelledNodOnce = true;

        Check(bridge.StopForTaskIfCurrent("controlled-waiting-stop", report.task, report.operation, out string failure, true), failure);
        report.stoppedOperation = bridge.Controller.OperationRevision;
        Check(!bridge.IsFollowing && !bridge.Controller.IsContinuousFollowActive, "Typed stop did not revoke the actual waiting task.");
        report.explicitStopRevoked = true;
        user.position += Vector3.forward * .2f;
        // The generic WaitFor asserts an active follow; this task has deliberately
        // been stopped, so observe cancellation and the gesture's return directly.
        float returnDeadline = Time.realtimeSinceStartup + 2f;
        while (upper.IsPlaying)
        {
            Check(!bridge.IsFollowing && !bridge.Controller.IsContinuousFollowActive &&
                Time.realtimeSinceStartup < returnDeadline,
                "Stopped task revived or the cancelled nod failed to finish its smooth return.");
            yield return null;
        }
        yield return Delay(.4f);
        Check(!bridge.IsFollowing && !bridge.Controller.IsBusy && bridge.Controller.StreamStepCount == 0 &&
            string.IsNullOrEmpty(bridge.Controller.LastRequestJson) && Vector3.Distance(heldRoot, avatar.transform.position) < .0001f,
            "Stopped lifecycle fixture resumed or moved after the user moved.");
        report.stopPreventedRestart = true; report.stopRootDrift = Vector3.Distance(heldRoot, avatar.transform.position);
    }

    private static IEnumerator MoveUser(float seconds, float speed, bool requirePlaying)
    {
        float start = Time.realtimeSinceStartup, previous = start;
        while (Time.realtimeSinceStartup - start < seconds)
        {
            float now = Time.realtimeSinceStartup, dt = Mathf.Min(.1f, now - previous); previous = now;
            user.position += Vector3.forward * (speed * dt);
            AssertSameTask();
            if (requirePlaying) Check(bridge.Controller.Player.HasPoseOwnership && !bridge.Controller.Player.IsReturningToAnimation,
                "An already walking continuous task returned the body while the user was still moving.");
            yield return null;
        }
    }

    private static void AssertSameTask()
    {
        Check(bridge.IsFollowing && bridge.Controller.IsContinuousFollowActive && bridge.ActiveTaskId == report.task &&
            bridge.Controller.OperationRevision == report.operation, "Continuous user control or waiting changed the accepted parent task/operation: " + bridge.Status);
        Check(bridge.Controller.StreamUnderrunCount == 0, "Chat follow ran out of fresh streaming motion.");
    }

    private static IEnumerator WaitFor(Func<bool> ready, float seconds, string label)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!ready())
        {
            AssertSameTask();
            Check(Time.realtimeSinceStartup < deadline, label + " timed out: " + bridge.Controller.Stage + " / " + bridge.Status);
            yield return null;
        }
    }
    private static IEnumerator Delay(float seconds)
    { float end = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < end) yield return null; }
    private static DialogueMotionIntent RoleCommand(string name)
    {
        Invoke(chat, "BeginFormalResponseGeneration", null);
        object[] executable = { "<motion name=\"" + name + "\"/>" };
        object parsed = Invoke(chat, "ExtractDialogueMotion", executable);
        Check(parsed is DialogueMotionIntent && string.IsNullOrWhiteSpace((string)executable[0]), "Chat did not parse and strip " + name);
        var intent = (DialogueMotionIntent)parsed;
        Invoke(chat, "DispatchDialogueMotion", new object[] { (DialogueMotionIntent?)intent, false });
        return intent;
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Chat stream batch exceeded its 90-second startup budget.")); return; }
        if (!EditorApplication.isPlaying || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            if (observationError != null) throw observationError;
            Check(Time.realtimeSinceStartup <= scenarioDeadline, "Short Chat stream integration exceeded its 30-second runtime budget.");
            Check(!chat.enabled && !avatar.enabled && vrmRuntime.GetValue(avatar) == null, "Chat services or VRM runtime started during the fixture.");
            report.chatServicesDormant = true;
            int budget = 50;
            while (routines.Count > 0 && budget-- > 0)
            {
                var top = routines.Peek();
                if (!top.MoveNext()) { (top as IDisposable)?.Dispose(); routines.Pop(); continue; }
                if (top.Current is IEnumerator child) { routines.Push(child); continue; }
                return;
            }
            if (routines.Count == 0) Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }
    private static void Observe()
    {
        if (!observe || bridge == null || !bridge.IsConnected || observationError != null) return;
        try
        {
            float now = Time.realtimeSinceStartup, dt = now - previousSampleAt;
            var controller = bridge.Controller; var player = controller.Player;
            float step = previousSampleAt > 0 ? Vector3.Distance(previousRoot, avatar.transform.position) : 0;
            report.maximumRootStep = Mathf.Max(report.maximumRootStep, step);
            Check(step < Mathf.Max(.15f, dt * 2.5f), "Chat stream root teleported between observed rendered frames.");
            if (countResumeCancel && previousLiveEnabled && !live.isActiveAndEnabled) report.resumeOwnerDisableCount++;
            previousLiveEnabled = live.isActiveAndEnabled;
            if (Time.frameCount % 3 == 0 || player.IsReturningToAnimation)
            {
                Check(report.samples.Count < 1600, "Short integration exceeded its bounded observation budget.");
                var facts = bridge.ReadCurrentTargetEvidence();
                report.samples.Add(new Sample { frame = Time.frameCount, realtime = now, observationPhase = observationPhase,
                    phase = bridge.Phase, stage = controller.Stage, task = bridge.ActiveTaskId, operation = controller.OperationRevision,
                    following = bridge.IsFollowing, continuous = controller.IsContinuousFollowActive, session = controller.StreamSessionId,
                    streamSteps = controller.StreamStepCount, streamControl = controller.StreamControlRevision, appliedControl = controller.StreamAppliedControlRevision,
                    underruns = controller.StreamUnderrunCount, bodyReserved = bridge.ReservesBody, poseOwned = player.HasPoseOwnership,
                    playing = player.IsPlaying, returning = player.IsReturningToAnimation, animatorEnabled = animator.enabled,
                    liveEnabled = live.isActiveAndEnabled, liveRevision = live.Revision, playbackRevision = player.PlaybackRevision,
                    userDistance = facts.actualUserDistance, estimatedUserSpeed = facts.followEstimatedUserSpeed,
                    requestedSpeed = facts.followRequestedSpeed, measuredSpeed = controller.StreamMeasuredSpeed, bufferSeconds = controller.StreamBufferSeconds,
                    root = avatar.transform.position, user = user.position });
            }
            previousSampleAt = now; previousRoot = avatar.transform.position;
        }
        catch (Exception error) { observationError = error; }
    }
    private static void InstallObserver(bool install)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop(); Rewrite(ref loop); PlayerLoop.SetPlayerLoop(loop);
        void Rewrite(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return;
            var children = system.subSystemList.Where(c => c.type != typeof(ArdyRoomChatStreamRuntimeRegression)).ToList();
            for (int i = 0; i < children.Count; i++) { var child = children[i]; Rewrite(ref child); children[i] = child; }
            if (install && system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                children.Add(new PlayerLoopSystem { type = typeof(ArdyRoomChatStreamRuntimeRegression), updateDelegate = Observe });
            system.subSystemList = children.ToArray();
        }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        observe = false; InstallObserver(false);
        if (report == null) report = new Report { unityVersion = Application.unityVersion };
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        report.duration = Time.realtimeSinceStartup - started;
        if (error != null) Debug.LogException(error);
        if (bridge != null && bridge.IsConnected)
        {
            report.finalFacts = bridge.DescribeRoomContext();
            if (!string.IsNullOrEmpty(bridge.Controller.LastRequestJson)) File.WriteAllText(Path.Combine(Output(), "last-request.json"), bridge.Controller.LastRequestJson);
            if (!string.IsNullOrEmpty(bridge.Controller.LastResponseJson)) File.WriteAllText(Path.Combine(Output(), "last-response.json"), bridge.Controller.LastResponseJson);
            if (!string.IsNullOrEmpty(bridge.Controller.LastErrorResponseJson)) File.WriteAllText(Path.Combine(Output(), "last-error-response.json"), bridge.Controller.LastErrorResponseJson);
        }
        File.WriteAllText(Path.Combine(Output(), "report.json"), JsonUtility.ToJson(report, true));
        if (bridge != null) bridge.Disconnect("chat-stream-fixture-finished");
        routines.Clear(); SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.Exit(SessionState.GetInt(Key + "ExitCode", 1));
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); if (report != null) report.checks++; }
    private static Vector3 Horizontal(Vector3 value) { value.y = 0; return value; }
    private static void SetField(object target, string name, object value)
    { (target.GetType().GetField(name, Private) ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value); }
    private static object Invoke(object target, string name, object[] arguments)
    {
        var method = target.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(target.GetType().Name, name);
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
    private static string Argument(string key, string fallback)
    { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
    private static string Output()
    { string path = SessionState.GetString(Key + "Output", "Logs/ardy-room-chat-stream-runtime"); Directory.CreateDirectory(path); return path; }
    private static string Hash(string path)
    { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
}
