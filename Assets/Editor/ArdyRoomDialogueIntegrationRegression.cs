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

/// <summary>Real runtime dialogue bridge, native HTTP generation and Animator in a disposable batch scene.</summary>
[InitializeOnLoad]
public static class ArdyRoomDialogueIntegrationRegression
{
    private const string Key = "NeEEvA.ARDY.RoomDialogueIntegration.";
    private const string AvatarName = "ARDY-Room-Dialogue-Avatar", ChatName = "ARDY-Room-Dialogue-Chat";
    private const string EnvironmentName = "ARDY-Room-Dialogue-Environment";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Serializable] private sealed class Source { public string path, sha256; }
    [Serializable] private sealed class Sample
    {
        public int frame, operationRevision;
        public float realtime, clipSeconds, userDistance, facingError;
        public string phase, executionState;
        public bool playing, returning, animatorEnabled, poseOwned;
        public Vector3 root, user;
        public Quaternion rotation;
    }
    [Serializable] private sealed class Case
    {
        public string name, phase, executionState, context, requestPath, responsePath, errorResponsePath;
        public bool completed;
        public int operationRevision, playingFrames, returningFrames, postReturnFrames;
        public float userDistance, facingError, travel, returnClipSeconds, selectedEndSeconds, plannedArrivalSeconds;
        public float rawLastSampleSeconds, returnDuration, returnRootDrift, firstReturnRotationStep, maximumReturnRotationStep;
        public float maximumReturnPositionStep, firstRestoredRotationStep;
        public Vector3 startRoot, endRoot, user;
        public List<Sample> samples = new List<Sample>();
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, roomScene, runtimeAssemblySha256, motionAssemblySha256, harnessAssemblySha256;
        public string scope = "Actual room Connect and ChatSample completed-motion parsing/dispatch; native ARDY HTTP, NavMesh, disabled VRM and actual Animator. Ordinary speech is injected at the production cancellation event boundary; TTS/ASR and role-model generation are not started.";
        public string limitations = "Bounded geometric and lifecycle integration; not subjective motion quality, acoustic speech recognition, continuous following, all-room coverage, or proof of server-side cancellation completion. A cancelled HTTP operation is monitored through subsequent runs for client-side stale playback.";
        public int checks, navigationSources, navigationProxies, roomTaskClosureChecks, roomFollowChecks, roomSpeechPreparationChecks;
        public bool extendedRuntime, continuityRuntime;
        public int retargetChecks;
        public bool connectedThroughRuntimeBridge, vrmDisabledThroughout, vrmRuntimeUninitialized, ordinarySpeechPreservedMovement;
        public bool staleRoleCallbackRejected, generationStopPreservedRoot, playingStopPreservedRoot, movingUserRetargeted;
        public List<Source> sources = new List<Source>();
        public List<Case> cases = new List<Case>();
    }

    private static Report report;
    private static Case current;
    private static ChatSample chat;
    private static ArdyRoomDialogueBridge bridge;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyLocomotionAvatarReference reference;
    private static Transform user;
    private static Vector3 originalUser, originalGround, heldRoot, returnRoot;
    private static Quaternion heldRotation;
    private static FieldInfo vrmRuntime;
    private static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
    private static readonly HumanBodyBones[] ObservedBones = {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot };
    private static Quaternion[] precedingRotations;
    private static Vector3[] precedingPositions;
    private static float returnStarted;
    private static int lastFrame;
    private static bool sawReturn, sawRestored;
    private static Exception observationError;

    static ArdyRoomDialogueIntegrationRegression()
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

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only use the isolated unity-naturalness-validation batch project, without -quit.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play mode already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a modified validation scene.");
            string asset = Argument("-ardyRoomAvatar", "Assets/Model/NeEEvA.vrm");
            string scene = Argument("-ardyRoomScene", "");
            if (scene.IndexOf("_Chat", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ArgumentException("Use the geometry-only room, never the user's private chat scene.");
            if (!string.IsNullOrEmpty(scene) && string.IsNullOrEmpty(Argument("-ardyRoomUser", "")))
                throw new ArgumentException("A real room requires an explicit world head position in -ardyRoomUser.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (prefab == null) throw new ArgumentException("Missing imported VRM: " + asset);
            var imported = ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), asset);
            SessionState.SetBool(Key + "Extended", bool.Parse(Argument("-ardyRoomExtended", "false")));
            SessionState.SetBool(Key + "Continuity", bool.Parse(Argument("-ardyRoomContinuity", "false")));
            SessionState.SetString(Key + "Avatar", asset);
            SessionState.SetString(Key + "Scene", scene);
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomDialogueOutput", "Logs/ardy-room-dialogue-integration")));
            SessionState.SetString(Key + "Endpoint", Argument("-ardyRoomEndpoint", "http://127.0.0.1:8093"));
            SessionState.SetString(Key + "User", Argument("-ardyRoomUser", "0,1.6,2.6"));
            SessionState.SetString(Key + "NearUser", Argument("-ardyRoomNearUser", ""));
            SessionState.SetString(Key + "Environment", Argument("-ardyRoomEnvironmentName", "===＝オブジェクト===＝"));
            SessionState.SetString(Key + "Exclusions", Argument("-ardyRoomExcludedVisualRoots", ""));
            if (string.IsNullOrEmpty(scene)) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            else EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            GameObject environment;
            if (string.IsNullOrEmpty(scene))
            {
                environment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                environment.name = EnvironmentName;
                environment.transform.SetPositionAndRotation(new Vector3(0, -.1f, 0), Quaternion.identity);
                environment.transform.localScale = new Vector3(12, .2f, 12);
            }
            else environment = GameObject.Find(SessionState.GetString(Key + "Environment", ""));
            if (environment == null) throw new InvalidOperationException("Missing explicit environment root.");
            ArdyRoomGeometryPreparation.Prepare(environment.transform, false);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            root.name = AvatarName;
            float scale = float.Parse(Argument("-ardyRoomAvatarScale", "1"), CultureInfo.InvariantCulture);
            if (!Finite(scale) || scale <= 0) throw new ArgumentException("Invalid avatar scale.");
            root.transform.localScale = Vector3.one * scale;
            Vector3 start = ParseVector(Argument("-ardyRoomStart", "0,0,0"));
            if (!bool.Parse(Argument("-ardyRoomStartIsRoot", "false"))) start.y -= imported.groundY * scale;
            root.transform.SetPositionAndRotation(start, Quaternion.Euler(0,
                float.Parse(Argument("-ardyRoomInitialYaw", "90"), CultureInfo.InvariantCulture), 0));
            root.GetComponent<Vrm10Instance>().enabled = false;
            foreach (var owner in root.GetComponentsInChildren<Animator>(true))
            { owner.enabled = false; owner.runtimeAnimatorController = null; owner.applyRootMotion = false; owner.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            // Disabled ChatSample preserves its real motion parser and event bus while its Start,
            // speech loop and service startup stay dormant. Satisfy Awake's ordinary UI bindings.
            var conversation = new GameObject(ChatName);
            conversation.SetActive(false);
            var sample = conversation.AddComponent<ChatSample>(); sample.enabled = false;
            var button = new GameObject("Fixture commit", typeof(RectTransform), typeof(Button)).GetComponent<Button>();
            button.transform.SetParent(conversation.transform, false);
            var text = new GameObject("Fixture text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            text.transform.SetParent(conversation.transform, false);
            SetField(sample, "m_CommitMsgBtn", button); SetField(sample, "m_TextBack", text);
            SetField(sample, "m_Animator", root.GetComponent<Animator>());
            SetField(sample, "m_PersistSubtitleSettings", false); SetField(sample, "m_EnableSubtitleTranslation", false);
            var room = conversation.AddComponent<ArdyRoomDialogueBridge>(); room.connectOnStart = false;
            conversation.SetActive(true);
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 300);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion, roomScene = SessionState.GetString(Key + "Scene", ""),
            runtimeAssemblySha256 = Hash(typeof(ArdyRoomDialogueBridge).Assembly.Location),
            motionAssemblySha256 = Hash(typeof(ArdyLocomotionPlayer).Assembly.Location),
            harnessAssemblySha256 = Hash(typeof(ArdyRoomDialogueIntegrationRegression).Assembly.Location) };
        foreach (var path in new[] { SessionState.GetString(Key + "Avatar", ""), AnimatorPath,
            "Assets/AIChatTookit/Scripts/Chat/ArdyRoomDialogueBridge.cs", "Assets/AIChatTookit/Scripts/Chat/ArdyDialogueMotionBridge.cs",
            "Assets/AIChatTookit/Scripts/Chat/ChatSample.Motion.cs", "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyRoomTargetBuilder.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomApproachPlanner.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomArrivalTiming.cs",
            "Assets/Editor/ArdyRoomDialogueIntegrationRegression.cs", report.roomScene })
            if (!string.IsNullOrEmpty(path)) report.sources.Add(new Source { path = path, sha256 = Hash(path) });
        avatar = GameObject.Find(AvatarName).GetComponent<Vrm10Instance>(); animator = avatar.GetComponent<Animator>();
        vrmRuntime = typeof(Vrm10Instance).GetField("m_runtime", Private);
        Check(vrmRuntime != null && !avatar.enabled && vrmRuntime.GetValue(avatar) == null, "Disabled VRM was initialized before binding.");
        reference = ArdyLocomotionAvatarReference.FromImportedPrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(SessionState.GetString(Key + "Avatar", "")).GetComponent<Vrm10Instance>(), SessionState.GetString(Key + "Avatar", ""));
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Check(animator.runtimeAnimatorController != null, "Missing actual idle Animator.");
        animator.enabled = true; animator.SetInteger("state", 0);
        chat = GameObject.Find(ChatName).GetComponent<ChatSample>(); bridge = chat.GetComponent<ArdyRoomDialogueBridge>();
        user = new GameObject("Explicit fixture user head").transform;
        user.position = originalUser = ParseVector(SessionState.GetString(Key + "User", ""));
        originalGround = avatar.transform.position + Vector3.up * reference.groundY * avatar.transform.lossyScale.y;
        bridge.chat = chat; bridge.avatar = avatar; bridge.userHead = user; bridge.importedReference = reference;
        bridge.environmentRoot = GameObject.Find(string.IsNullOrEmpty(report.roomScene) ? EnvironmentName : SessionState.GetString(Key + "Environment", "")).transform;
        bridge.serviceUrl = SessionState.GetString(Key + "Endpoint", "");
        bridge.excludedVisualRoots = SessionState.GetString(Key + "Exclusions", "").Split('|').Where(s => !string.IsNullOrEmpty(s))
            .Select(name => { var found = GameObject.Find(name); if (found == null) throw new InvalidOperationException("Missing explicit exclusion: " + name); return found.transform; }).ToArray();
        bridge.Connect();
        report.connectedThroughRuntimeBridge = bridge.IsConnected && chat.GetComponent<ArdyDialogueMotionBridge>().IsBound;
        Check(report.connectedThroughRuntimeBridge && !chat.enabled && chat.RoomMotionOutputEnabled, "Runtime Connect failed or chat services were enabled.");
        report.navigationSources = bridge.Navigation.SourceCount; report.navigationProxies = bridge.Navigation.GeometryProxyCount;
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        routines.Clear(); routines.Push(Scenario()); observationError = null; lastFrame = -1;
        InstallObserver(true);
    }

    private static IEnumerator Scenario()
    {
        yield return Delay(.4f);
        if (Environment.GetCommandLineArgs().Contains("-ardyPreparationOnly"))
        {
            report.roomSpeechPreparationChecks = ArdyRoomSpeechPreparationRegression.RunChecks(chat, bridge);
            report.scope = "Connected room preparation ownership and finite recovery with controlled callbacks; no motion-generation or audible-latency claim.";
            yield break;
        }
        bool retargetOnly = Environment.GetCommandLineArgs().Contains("-ardyApproachRetargetOnly");
        if (!retargetOnly) report.roomTaskClosureChecks = ArdyRoomTaskClosureRegression.RunChecks(chat, bridge);
        report.roomFollowChecks = ArdyRoomFollowRegression.RunChecks(chat, bridge);
        if (!retargetOnly) report.roomSpeechPreparationChecks = ArdyRoomSpeechPreparationRegression.RunChecks(chat, bridge);
        if (SessionState.GetBool(Key + "Continuity", false))
        {
            report.continuityRuntime = true;
            report.scope = "Actual ARDY rolling follow on an isolated flat floor at ordinary speed, plus mid-walk smooth stop with real Animator frames. Child reports contain continuous target updates, native HTTP and measured root/pose observations.";
            report.limitations = "Bounded flat-floor following and pose continuity, not all-room, fast target pursuit, subjective naturalness or acoustic stop latency.";
            report.retargetChecks = ArdyRoomRetargetRegression.RunChecks();
            bridge.StopMovement();
            yield return WaitFor(() => !bridge.ReservesBody, 2f, "fixture pose release");
            yield return ArdyRoomRetargetFailureRegression.RunRuntimeChecks(bridge.Controller,
                originalGround + Vector3.forward * 4f, Path.Combine(Output(), "retarget-failures"));
            yield return ArdyRoomFollowRuntimeRegression.RunRuntimeChecks(chat, bridge, user);
            Check(ArdyRoomFollowRuntimeRegression.Completed, "Continuous native follow did not complete.");
            yield return WaitFor(() => !bridge.ReservesBody, 2f, "follow fixture pose release");
            yield return ArdyLocomotionSmoothStopRegression.RunRuntimeChecks(bridge.Controller.Player,
                bridge.Controller.LastClip, Output());
            yield break;
        }
        if (SessionState.GetBool(Key + "Extended", false))
        {
            report.extendedRuntime = true;
            report.scope = "Actual connected room segmented locomotion plus strict follow task and speech preparation lifecycle regressions. Rolling follow playback is exercised separately in ContinuityRuntime on a longer flat-floor fixture.";
            report.limitations = "Fixed room and supported low-speed long-duration route, not all-room coverage, predictive pursuit or live acoustic stop latency.";
            bridge.StopMovement();
            Vector3 target = ParseVector(Argument("-ardyRoomTarget", "-2.480432,-0.704,-6.417353"));
            yield return ArdyRoomSegmentRuntimeRegression.RunRuntimeChecks(bridge.Controller, target, Path.Combine(Output(), "segments"));
            yield break;
        }
        Check(animator.GetBoneTransform(HumanBodyBones.Hips) == avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips), "Idle Animator does not target the actual Humanoid.");
        BeginCase("same-frame-runtime-reconnect");
        HoldRoot(); bridge.Connect(); bridge.Connect();
        Check(bridge.IsConnected && bridge.Phase == "ready" && chat.RoomMotionOutputEnabled,
            "Same-frame reconnect retained a stale controller or lost the room channel.");
        var originalNavigation = bridge.Navigation;
        bool refusedSecondSession = false;
        try { ArdyRoomLocomotionWindow.EnsureAvatarAvailable(avatar, null); }
        catch (InvalidOperationException) { refusedSecondSession = true; }
        Check(refusedSecondSession && bridge.IsConnected && bridge.Navigation == originalNavigation,
            "Point-selection setup must reject a second session without disposing the connected chat navigation.");
        ArdyRoomLocomotionWindow.EnsureAvatarAvailable(avatar, bridge.Controller);
        CheckHeldRoot(); EndCase();
        BeginCase("read-only-current-target-evidence");
        HoldRoot(); int previewOperation = bridge.Controller.OperationRevision;
        var initialEvidence = bridge.ReadCurrentTargetEvidence(true);
        Check(initialEvidence.assessment.state == "reachable" && initialEvidence.assessment.canPlanRoute &&
            !initialEvidence.assessment.budgetChecked && !initialEvidence.assessment.executionGuaranteed &&
            initialEvidence.attemptStatus == "no-attempt", "Initial route preview confused feasibility with an executed action.");
        var cachedEvidence = bridge.ReadCurrentTargetEvidence();
        Check(cachedEvidence.currentTargetRevision == initialEvidence.currentTargetRevision &&
            cachedEvidence.assessment.buildCount == initialEvidence.assessment.buildCount,
            "Repeated read-only context rebuilt routes or changed the target revision.");
        initialEvidence.assessment.reason = "mutated external copy";
        Check(bridge.ReadCurrentTargetEvidence().assessment.reason != initialEvidence.assessment.reason,
            "A caller could mutate the bridge's cached current assessment.");
        int oldTargetRevision = cachedEvidence.currentTargetRevision;
        user.position += Vector3.right * .1f;
        Check(!bridge.ApproachForTarget("fixture:stale-target", oldTargetRevision, out string staleReason) &&
            !string.IsNullOrEmpty(staleReason) && bridge.Controller.OperationRevision == previewOperation && !bridge.ReservesBody,
            "A stale structured decision submitted or cancelled body work.");
        user.position = originalUser;
        var restoredEvidence = bridge.ReadCurrentTargetEvidence(true);
        Check(restoredEvidence.currentTargetRevision != oldTargetRevision && restoredEvidence.assessment.canPlanRoute &&
            restoredEvidence.attemptStatus == "no-attempt", "Restoring an old coordinate reused an old target revision or invented an attempt.");
        bridge.InvalidateCurrentTargetAssessment();
        var invalidatedEvidence = bridge.ReadCurrentTargetEvidence();
        Check(invalidatedEvidence.currentTargetRevision > restoredEvidence.currentTargetRevision &&
            invalidatedEvidence.assessment.buildCount > restoredEvidence.assessment.buildCount &&
            bridge.Controller.OperationRevision == previewOperation, "Explicit room changes did not invalidate the read-only cache.");
        CheckHeldRoot(); EndCase();
        BeginCase("stop-during-real-generation");
        var stale = RoleCommand("approach");
        yield return WaitFor(() => bridge.Controller.ExecutionState == ArdyRoomExecutionState.Generating, 30, "real generation submission");
        Check(!string.IsNullOrEmpty(bridge.Controller.LastRequestJson), "Generation cancellation never reached a real HTTP request.");
        var busyEvidence = bridge.ReadCurrentTargetEvidence();
        var busyEvidenceAgain = bridge.ReadCurrentTargetEvidence(true);
        Check(busyEvidence.assessment.state == "busy" && busyEvidenceAgain.assessment.state == "busy" &&
            busyEvidence.currentTargetRevision == busyEvidenceAgain.currentTargetRevision &&
            busyEvidence.assessment.buildCount == busyEvidenceAgain.assessment.buildCount,
            "Reading an active task restarted route planning or changed its target identity.");
        SaveWire(); HoldRoot(); RoleCommand("stop-moving");
        CheckStopped();
        Invoke(chat, "DispatchDialogueMotion", new object[] { (DialogueMotionIntent?)stale, false });
        Check(bridge.Phase == "stopped" && !bridge.ReservesBody, "Stale role callback restarted a cancelled movement.");
        report.staleRoleCallbackRejected = true;
        yield return Delay(1f); CheckHeldRoot(); report.generationStopPreservedRoot = true; EndCase();

        BeginCase("approach-through-ordinary-speech");
        RoleCommand("approach");
        int operation = bridge.Controller.OperationRevision;
        OrdinarySpeech(operation);
        yield return WaitFor(() => bridge.Controller.Player.IsPlaying, 40, "normal approach playback");
        OrdinarySpeech(operation);
        user.position -= Vector3.up * .45f;
        yield return Delay(.10f);
        Check(bridge.ReservesBody && bridge.Controller.OperationRevision == operation,
            "A valid desktop view-height adjustment cancelled the unchanged horizontal destination.");
        user.position += Vector3.up * .45f;
        // A following sentence without a movement tag also has no right to cancel the spatial goal.
        Invoke(chat, "BeginFormalResponseGeneration", null);
        Check(bridge.Controller.OperationRevision == operation && bridge.ReservesBody, "Speech-only response replaced the spatial operation.");
        report.ordinarySpeechPreservedMovement = true;
        yield return WaitFor(() => !bridge.ReservesBody, 20, "normal arrival");
        yield return Delay(.35f);
        ValidateArrival(false); SaveWire(); EndCase();

        BeginCase("nearby-native-facing-only");
        user.position = FindUser(true);
        Vector3 beforeFacing = avatar.transform.position;
        RoleCommand("approach");
        Check(bridge.Controller.Route != null && bridge.Controller.Route.Length < .08f, "Nearby facing unexpectedly requested travel.");
        yield return WaitFor(() => !bridge.ReservesBody, 40, "native in-place facing");
        yield return Delay(.35f);
        ValidateArrival(true); SaveWire();
        Check(Horizontal(avatar.transform.position - beforeFacing).magnitude <= .20f, "Facing-only action translated beyond endpoint tolerance.");
        var targets = (JArray)JObject.Parse(bridge.Controller.LastRequestJson)["targets"];
        Check(targets.All(t => JToken.DeepEquals(t["rootPosition"], targets[0]["rootPosition"])), "Facing-only HTTP constraints contain root translation.");
        EndCase();

        BeginCase("already-near-and-facing-no-op");
        HoldRoot(); RoleCommand("approach");
        Check(bridge.Phase == "arrived" && bridge.Controller.ExecutionReason == ArdyRoomExecutionReason.AlreadyAtTarget &&
            string.IsNullOrEmpty(bridge.Controller.LastRequestJson), "Already-near request generated unnecessary movement.");
        yield return Delay(.25f); CheckHeldRoot(); EndCase();

        BeginCase("unreachable-user-support");
        HoldRoot(); user.position = avatar.transform.position + Vector3.up * 10;
        RoleCommand("approach");
        Check(bridge.Phase == "unreachable" && !bridge.ReservesBody && !bridge.Controller.IsBusy,
            "Unsupported user head position was not explicitly rejected.");
        var unsupported = JObject.Parse(bridge.DescribeRoomContext());
        Check(!(bool)unsupported["userAnchorValid"] && !(bool)unsupported["currentlyNearAndFacingUser"],
            "Unsupported user still produced a current near-and-facing fact.");
        Check((string)unsupported["latestExecutionObservation"]["stage"] == "user-target-validation" &&
            (string)unsupported["latestExecutionObservation"]["actionId"] == bridge.ActionId &&
            !(bool)unsupported["currentUserSupport"]["found"] && bridge.RecentDiagnosticText.Contains(bridge.ActionId),
            "A target rejected before generation must expose its own action identity and measured support diagnostics.");
        yield return Delay(.25f); CheckHeldRoot(); EndCase();

        BeginCase("new-target-does-not-inherit-old-rejection");
        HoldRoot(); int rejectedTarget = bridge.ReadCurrentTargetEvidence().currentTargetRevision;
        string rejectedAction = bridge.ActionId;
        user.position = FindUser(false);
        var newTarget = JObject.Parse(bridge.DescribeRoomContext());
        Check((int)newTarget["currentTargetRevision"] != rejectedTarget &&
            !(bool)newTarget["lastAttemptAppliesToCurrentTarget"] &&
            (string)newTarget["currentFacts"]["attemptStatus"] == "no-attempt" &&
            (bool)newTarget["currentAssessment"]["canPlanRoute"] &&
            (string)newTarget["lastActionResults"]["actionId"] == rejectedAction &&
            (string)newTarget["lastActionResults"]["phase"] == "unreachable",
            "Moving to a feasible user position inherited the former rejection or manufactured a new action result.");
        Check(bridge.Phase == "unreachable" && !bridge.ReservesBody, "Read-only new target assessment dispatched movement.");
        CheckHeldRoot(); EndCase();

        BeginCase("self-anchor-cannot-become-user");
        HoldRoot(); bridge.userHead = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Head);
        RoleCommand("approach");
        var self = JObject.Parse(bridge.DescribeRoomContext());
        Check(bridge.Phase == "unavailable" && !bridge.ReservesBody && !(bool)self["userAnchorValid"] &&
            !(bool)self["currentlyNearAndFacingUser"], "Avatar's own head was accepted as the user anchor.");
        CheckHeldRoot(); EndCase(); bridge.userHead = user;

        BeginCase("continuous-target-updates-coalesce-before-first-playback");
        user.position = FindUser(false, true); RoleCommand("approach");
        string preparingAction = bridge.ActionId;
        Vector3 lateral = Vector3.Cross(Vector3.up, Horizontal(user.position - avatar.transform.position).normalized);
        for (int update = 0; update < 8; update++)
        {
            user.position += lateral * .20f;
            yield return Delay(.045f);
            if (!bridge.Controller.Player.IsPlaying && bridge.Controller.CommittedTargetRevision == 0)
                Check(bridge.ReadCurrentTargetEvidence().approachRetargetCount == 0,
                    "Queued preparation samples consumed actual route-attempt budget before any movement.");
            Check(bridge.Phase != "blocked" && bridge.Phase != "cancelled", "Preparation updates prematurely exhausted an approach.");
        }
        Check(bridge.ReadCurrentTargetEvidence().approachRetargetUpdates >= 3,
            "The fixture must cover multiple material target updates, not only one coalesced sample.");
        yield return WaitFor(() => bridge.Phase == "arrived" && !bridge.ReservesBody, 50, "coalesced preparation arrival");
        Check(bridge.ActionId == preparingAction && !bridge.IsFollowing && bridge.ReadCurrentTargetEvidence().currentlyNearAndFacingUser,
            "Coalesced preparation must arrive at the latest target under the same bounded action.");
        EndCase();

        BeginCase("moving-user-retargets-pending-operation");
        user.position = FindUser(false, true); RoleCommand("approach");
        yield return WaitFor(() => bridge.Controller.ExecutionState == ArdyRoomExecutionState.Generating, 30, "moving-user fixture generation");
        SaveWire(); string retargetAction = bridge.ActionId;
        user.position += Vector3.Cross(Vector3.up, Horizontal(user.position - avatar.transform.position).normalized) * .5f;
        yield return WaitFor(() => bridge.ReadCurrentTargetEvidence().approachRetargetUpdates > 0, 2, "bounded target refresh");
        Check(bridge.ActionId == retargetAction && !bridge.IsFollowing && bridge.Phase != "cancelled", "Small target drift cancelled the parent or enabled continuous following.");
        yield return WaitFor(() => bridge.Phase == "arrived" && !bridge.ReservesBody, 50, "retarget arrival");
        Check(bridge.ActionId == retargetAction && bridge.ReadCurrentTargetEvidence().approachRetargetCount <= 3, "Recovery created a new logical task or exceeded its count budget.");
        HoldRoot(); user.position += Vector3.right * .6f;
        yield return Delay(1f);
        Check(bridge.Phase == "arrived" && !bridge.ReservesBody && !bridge.IsFollowing && bridge.ActionId == retargetAction,
            "Moving after arrival rewrote history or silently restarted the old approach.");
        CheckHeldRoot(); report.movingUserRetargeted = true; EndCase();

        BeginCase("moving-user-retargets-playing-operation");
        user.position = FindUser(false, true); RoleCommand("approach");
        yield return WaitFor(() => bridge.Controller.Player.IsPlaying && bridge.Controller.Player.TimeSeconds > .2f, 40, "retarget while moving");
        retargetAction = bridge.ActionId;
        user.position += Vector3.Cross(Vector3.up, Horizontal(user.position - avatar.transform.position).normalized) * .5f;
        yield return WaitFor(() => bridge.ReadCurrentTargetEvidence().approachRetargetCount > 0, 2, "playing target refresh");
        yield return WaitFor(() => bridge.Phase == "arrived" && !bridge.ReservesBody, 50, "moving retarget arrival");
        Check(bridge.ActionId == retargetAction && !bridge.IsFollowing, "Playing recovery changed its parent identity or became following.");
        EndCase();

        BeginCase("late-subthreshold-target-shift");
        user.position = FindUser(false, true); RoleCommand("approach");
        yield return WaitFor(() => bridge.Controller.ExecutionState == ArdyRoomExecutionState.Returning, 40, "final pose return before arrival");
        retargetAction = bridge.ActionId;
        user.position += Horizontal(user.position - avatar.transform.position).normalized * .30f;
        yield return WaitFor(() => bridge.ReadCurrentTargetEvidence().approachRetargetCount > 0, 2, "subthreshold goal correction");
        yield return WaitFor(() => bridge.Phase == "arrived" && !bridge.ReservesBody, 50, "subthreshold corrected arrival");
        Check(bridge.ActionId == retargetAction && !bridge.IsFollowing &&
            bridge.ReadCurrentTargetEvidence().currentlyNearAndFacingUser,
            "A late shift below 35cm must complete under the same finite action, not fail or silently follow.");
        EndCase();

        BeginCase("one-shot-target-refresh-has-finite-travel");
        user.position = FindUser(false, true); RoleCommand("approach");
        yield return WaitFor(() => bridge.Controller.ExecutionState == ArdyRoomExecutionState.Generating, 30, "budget fixture generation");
        user.position += Vector3.Cross(Vector3.up, Horizontal(user.position - avatar.transform.position).normalized) * 3.2f;
        yield return WaitFor(() => !bridge.ReservesBody, 3, "bounded travel stop");
        Check(bridge.ReadCurrentTargetEvidence().approachRetargetStatus == "exhausted" && bridge.Phase != "arrived" && !bridge.IsFollowing,
            "A one-shot approach pursued unlimited target movement.");
        HoldRoot(); yield return Delay(.5f); CheckHeldRoot(); EndCase();

        BeginCase("stop-during-actual-playback");
        // This fixture tests cancellation of ordinary travel, not the separate overlapping-
        // user retreat policy. Keep the initial root well outside the user's reserved disk.
        user.position = FindUser(false, true); RoleCommand("approach");
        yield return WaitFor(() => bridge.Controller.Player.IsPlaying && bridge.Controller.Player.TimeSeconds > .2f, 40, "explicit-stop playback");
        SaveWire(); HoldRoot(); long playerRevision = bridge.Controller.Player.PlaybackRevision;
        RoleCommand("stop-moving");
        Check(bridge.Phase == "stopped" && !bridge.Controller.IsBusy && !bridge.Controller.Player.IsPlaying &&
            bridge.ReservesBody && bridge.Controller.Player.IsStoppingSmoothly && animator.enabled,
            "Explicit stop did not freeze travel while preserving the smooth return owner.");
        CheckHeldRoot();
        yield return WaitFor(() => !bridge.ReservesBody, 1.5f, "explicit stop pose return");
        CheckStopped();
        Check(current.returningFrames >= 8 && current.returnRootDrift < .0001f &&
            current.firstReturnRotationStep < 1f && current.maximumReturnRotationStep < 20f,
            "Explicit stop did not produce a measured smooth, stationary return.");
        yield return Delay(1f); CheckHeldRoot();
        Check(bridge.Controller.Player.PlaybackRevision >= playerRevision && !bridge.Controller.Player.HasPoseOwnership,
            "A stale generation reclaimed ownership after explicit stop.");
        report.playingStopPreservedRoot = true; EndCase();
        Check(report.cases.Count == 15 && report.cases.All(value => value.completed), "Expected all fifteen bounded integration cases to complete.");
    }

    private static DialogueMotionIntent RoleCommand(string name)
    {
        Invoke(chat, "BeginFormalResponseGeneration", null);
        object[] text = { "<motion name=\"" + name + "\"/>" };
        object parsed = Invoke(chat, "ExtractDialogueMotion", text);
        Check(parsed is DialogueMotionIntent && string.IsNullOrWhiteSpace((string)text[0]), "Actual ChatSample failed to parse/strip " + name);
        var intent = (DialogueMotionIntent)parsed;
        Invoke(chat, "DispatchDialogueMotion", new object[] { (DialogueMotionIntent?)intent, false });
        return intent;
    }

    private static void OrdinarySpeech(int operation)
    {
        foreach (string cause in new[] { "user-started-speaking", "new-user-turn", "barge-in" })
        {
            Invoke(chat, "CancelDialogueMotion", new object[] { cause });
            Check(bridge.ReservesBody && bridge.Controller.OperationRevision == operation &&
                bridge.Controller.ExecutionState != ArdyRoomExecutionState.Cancelled,
                "Ordinary dialogue cancellation stopped room movement: " + cause);
        }
    }

    private static Vector3 FindUser(bool nearby, bool ordinaryApproach = false)
    {
        string explicitNear = SessionState.GetString(Key + "NearUser", "");
        var candidates = new List<Vector3>();
        if (nearby && !string.IsNullOrEmpty(explicitNear)) candidates.Add(ParseVector(explicitNear));
        else
        {
            if (!nearby) { candidates.Add(originalGround + Vector3.up * 1.6f); candidates.Add(originalUser); }
            foreach (float radius in nearby ? new[] { 1f } : new[] { 2.2f, 1.8f, 1.5f })
                foreach (float angle in new[] { 90f, -90f, 60f, -60f, 120f, -120f, 180f, 30f, -30f, 0f })
                    candidates.Add(avatar.transform.position + Quaternion.AngleAxis(angle, Vector3.up) * avatar.transform.forward * radius + Vector3.up * 1.6f);
        }
        Vector3 saved = user.position;
        foreach (var candidate in candidates)
        {
            user.position = candidate;
            Vector3 ground = avatar.transform.position + Vector3.up * reference.groundY * avatar.transform.lossyScale.y;
            if (!ArdyRoomApproachPlanner.TryPlan(bridge.Navigation, ground, user, 1f, out var plan, out _)) continue;
            if (ordinaryApproach && Horizontal(ground - plan.UserGround).magnitude <
                Mathf.Max(plan.DesiredDistance + .35f, plan.UserClearanceRadius + .25f)) continue;
            float angle = Vector3.Angle(Horizontal(user.position - avatar.transform.position), Horizontal(avatar.transform.forward));
            if (nearby ? plan.AlreadyInRange && angle > 25f : plan.Route.Length > .30f)
            { user.position = saved; return candidate; }
        }
        user.position = saved;
        throw new InvalidOperationException("No audited " + (nearby ? "nearby facing" : "new travel") + " user fixture was available. Supply -ardyRoomNearUser or choose a less confined initial fixture.");
    }

    private static void ValidateArrival(bool onlyFacing)
    {
        var controller = bridge.Controller; var player = controller.Player;
        var fact = JObject.Parse(bridge.DescribeRoomContext());
        Check(bridge.Phase == "arrived" && controller.ExecutionState == ArdyRoomExecutionState.Arrived,
            "Actual arrival failed: " + bridge.Phase + " / " + bridge.Status);
        Check((float)fact["actualUserDistance"] >= .75f && (float)fact["actualUserDistance"] <= 1.25f &&
            (float)fact["actualFacingErrorDegrees"] <= 15f, "Actual user distance/facing missed the approach goal.");
        Check(!player.HasPoseOwnership && animator.enabled && player.ReturnedToAnimation,
            "Natural arrival did not return body ownership to the real idle Animator.");
        Check(current.playingFrames > 5 && current.returningFrames >= 8 && current.postReturnFrames >= 8,
            "Native playback and multi-frame idle handoff were not actually observed.");
        current.rawLastSampleSeconds = controller.LastClip.LastSampleSeconds;
        current.selectedEndSeconds = player.PlaybackEndSeconds;
        current.plannedArrivalSeconds = controller.LastArrivalTiming.PlannedArrivalSeconds;
        Check(Mathf.Abs(current.returnClipSeconds - player.PlaybackEndSeconds) < .0001f,
            "Selected zero-extra-hold endpoint was followed by additional playback delay.");
        Check(current.returnDuration >= .25f && current.returnDuration <= .75f && current.returnRootDrift < .0001f &&
            current.firstReturnRotationStep < 1f && current.maximumReturnRotationStep < 20f && current.maximumReturnPositionStep < .09f &&
            current.firstRestoredRotationStep < 10f, "Real Animator arrival handoff has an abrupt pose change or root drift.");
        Check(animator.GetCurrentAnimatorStateInfo(0).normalizedTime > 0 && !chat.enabled, "Actual Animator stopped or dormant chat was enabled.");
        if (!onlyFacing) Check(current.travel > .30f, "Normal approach did not visibly travel through the room.");
    }

    private static void BeginCase(string name)
    {
        current = new Case { name = name, startRoot = avatar.transform.position };
        report.cases.Add(current); precedingRotations = null; precedingPositions = null; sawReturn = sawRestored = false;
    }
    private static void EndCase()
    {
        current.completed = true; current.phase = bridge.Phase; current.executionState = bridge.Controller.ExecutionState.ToString();
        current.context = bridge.DescribeRoomContext(); current.endRoot = avatar.transform.position; current.user = user.position;
        var fact = JObject.Parse(current.context); current.userDistance = (float)fact["actualUserDistance"]; current.facingError = (float)fact["actualFacingErrorDegrees"];
        current.operationRevision = bridge.Controller.OperationRevision;
        File.WriteAllText(Path.Combine(Output(), "report.json"), JsonUtility.ToJson(report, true));
        current = null;
    }
    private static void HoldRoot() { heldRoot = avatar.transform.position; heldRotation = avatar.transform.rotation; }
    private static void CheckHeldRoot() => Check(Vector3.Distance(heldRoot, avatar.transform.position) < .0001f &&
        Quaternion.Angle(heldRotation, avatar.transform.rotation) < .05f, "Cancelled/stale operation changed the preserved root.");
    private static void CheckStopped() => Check(bridge.Phase == "stopped" && !bridge.ReservesBody && !bridge.Controller.IsBusy &&
        !bridge.Controller.Player.HasPoseOwnership && animator.enabled, "Explicit stop-moving retained pending work or body ownership.");

    private static IEnumerator Delay(float seconds)
    { float until = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < until) yield return null; }
    private static IEnumerator WaitFor(Func<bool> predicate, float timeout, string label)
    {
        float until = Time.realtimeSinceStartup + timeout;
        while (!predicate())
        {
            if (bridge.Phase == "failed" || bridge.Phase == "blocked" || bridge.Phase == "unreachable" || bridge.Phase == "goal-not-reached")
                throw new InvalidOperationException(label + " failed: " + bridge.Phase + " / " + bridge.Status);
            if (Time.realtimeSinceStartup > until) throw new TimeoutException(label + " timed out: " + bridge.Phase + " / " + bridge.Status);
            yield return null;
        }
    }
    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Room dialogue integration exceeded its 300-second budget.")); return; }
        if (!EditorApplication.isPlaying || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            if (observationError != null) throw observationError;
            Check(!chat.enabled && !avatar.enabled && vrmRuntime.GetValue(avatar) == null, "Chat/VRM lifecycle mode changed during integration.");
            report.vrmDisabledThroughout = report.vrmRuntimeUninitialized = true;
            int budget = 100;
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

    private static void InstallObserver(bool install)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop(); Rewrite(ref loop); PlayerLoop.SetPlayerLoop(loop);
        void Rewrite(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return;
            var children = system.subSystemList.Where(c => c.type != typeof(ArdyRoomDialogueIntegrationRegression)).ToList();
            for (int i = 0; i < children.Count; i++) { var child = children[i]; Rewrite(ref child); children[i] = child; }
            if (install && system.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                children.Add(new PlayerLoopSystem { type = typeof(ArdyRoomDialogueIntegrationRegression), updateDelegate = Observe });
            system.subSystemList = children.ToArray();
        }
    }
    private static void Observe()
    {
        if (current == null || bridge == null || !bridge.IsConnected || observationError != null) return;
        try
        {
            var player = bridge.Controller.Player;
            var bones = ObservedBones.Select(avatar.Humanoid.GetBoneTransform).Where(t => t != null).ToArray();
            var rotations = bones.Select(t => t.localRotation).ToArray(); var positions = bones.Select(t => t.localPosition).ToArray();
            float rotationStep = 0, positionStep = 0;
            if (precedingRotations != null)
                for (int i = 0; i < rotations.Length; i++)
                { rotationStep = Mathf.Max(rotationStep, Quaternion.Angle(precedingRotations[i], rotations[i])); positionStep = Mathf.Max(positionStep, Vector3.Distance(precedingPositions[i], positions[i])); }
            current.travel = Mathf.Max(current.travel, Vector3.Distance(current.startRoot, avatar.transform.position));
            if (player.IsPlaying) { current.playingFrames++; Check(!animator.enabled, "Animator contested native full-body playback."); }
            if (player.IsReturningToAnimation)
            {
                current.returningFrames++;
                Check(animator.enabled && player.HasPoseOwnership, "Return lost live Animator evaluation or body ownership.");
                if (!sawReturn)
                { sawReturn = true; returnStarted = Time.realtimeSinceStartup; returnRoot = avatar.transform.position; current.returnClipSeconds = player.TimeSeconds; current.firstReturnRotationStep = rotationStep; }
            }
            if (sawReturn)
            {
                current.returnRootDrift = Mathf.Max(current.returnRootDrift, Vector3.Distance(returnRoot, avatar.transform.position));
                current.maximumReturnRotationStep = Mathf.Max(current.maximumReturnRotationStep, rotationStep);
                current.maximumReturnPositionStep = Mathf.Max(current.maximumReturnPositionStep, positionStep);
                if (player.ReturnedToAnimation && !player.HasPoseOwnership)
                {
                    current.postReturnFrames++;
                    if (!sawRestored) { sawRestored = true; current.returnDuration = Time.realtimeSinceStartup - returnStarted; current.firstRestoredRotationStep = rotationStep; }
                }
            }
            if (Time.frameCount % 6 == 0 || player.IsReturningToAnimation)
            {
                Check(current.samples.Count < 1500, "Bounded frame observation exceeded 1500 samples per case.");
                var direction = Horizontal(user.position - avatar.transform.position);
                current.samples.Add(new Sample { frame = Time.frameCount, realtime = Time.realtimeSinceStartup, clipSeconds = player.TimeSeconds,
                    operationRevision = bridge.Controller.OperationRevision, phase = bridge.Phase, executionState = bridge.Controller.ExecutionState.ToString(),
                    root = avatar.transform.position, rotation = avatar.transform.rotation, user = user.position,
                    userDistance = direction.magnitude, facingError = Vector3.Angle(Horizontal(avatar.transform.forward), direction),
                    playing = player.IsPlaying, returning = player.IsReturningToAnimation, animatorEnabled = animator.enabled, poseOwned = player.HasPoseOwnership });
            }
            precedingRotations = rotations; precedingPositions = positions;
        }
        catch (Exception error) { observationError = error; }
    }

    private static void SaveWire()
    {
        if (current == null || bridge == null || bridge.Controller == null) return;
        var controller = bridge.Controller;
        if (!string.IsNullOrEmpty(controller.LastRequestJson))
        {
            current.requestPath = current.name + "-request.json";
            File.WriteAllText(Path.Combine(Output(), current.requestPath), controller.LastRequestJson);
            Check(JObject.Parse(controller.LastRequestJson)["initialHistory"]["frames"].Count() == 16, "HTTP request has no actual 16-frame history.");
        }
        if (!string.IsNullOrEmpty(controller.LastResponseJson))
        { current.responsePath = current.name + "-response.json"; File.WriteAllText(Path.Combine(Output(), current.responsePath), controller.LastResponseJson); }
        if (!string.IsNullOrEmpty(controller.LastErrorResponseJson))
        { current.errorResponsePath = current.name + "-error-response.json"; File.WriteAllText(Path.Combine(Output(), current.errorResponsePath), controller.LastErrorResponseJson); }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        InstallObserver(false);
        if (report == null) report = new Report { unityVersion = Application.unityVersion };
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        if (error != null) Debug.LogException(error);
        if (current != null && bridge != null && bridge.IsConnected)
        { SaveWire(); current.phase = bridge.Phase; current.context = bridge.DescribeRoomContext(); current.executionState = bridge.Controller.ExecutionState.ToString(); }
        File.WriteAllText(Path.Combine(Output(), "report.json"), JsonUtility.ToJson(report, true));
        if (bridge != null) bridge.Disconnect("fixture-finished");
        routines.Clear();
        SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.Exit(SessionState.GetInt(Key + "ExitCode", 1));
    }
    private static object Invoke(object target, string name, object[] arguments)
    {
        var method = target.GetType().GetMethod(name, Private);
        if (method == null) throw new MissingMethodException(target.GetType().Name, name);
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
    private static void SetField(object target, string name, object value)
    { var field = target.GetType().GetField(name, Private); if (field == null) throw new MissingFieldException(target.GetType().Name, name); field.SetValue(target, value); }
    private static string Output()
    { string path = SessionState.GetString(Key + "Output", "Logs/ardy-room-dialogue-integration"); Directory.CreateDirectory(path); return path; }
    private static string Argument(string name, string fallback)
    { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static Vector3 ParseVector(string value)
    {
        var parts = value.Split(','); if (parts.Length != 3) throw new ArgumentException("Expected world x,y,z coordinate.");
        var point = new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
        if (!Finite(point.x) || !Finite(point.y) || !Finite(point.z)) throw new ArgumentException("Coordinate must be finite."); return point;
    }
    private static Vector3 Horizontal(Vector3 value) => new Vector3(value.x, 0, value.z);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string Hash(string path)
    { using (var stream = File.OpenRead(Path.GetFullPath(path))) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void Check(bool condition, string message)
    { if (report != null) report.checks++; if (!condition) throw new InvalidOperationException(message); }
}
