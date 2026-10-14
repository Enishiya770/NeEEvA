using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;

/// <summary>Real ARDY delay/fencing regression in the existing disposable batch project.</summary>
[InitializeOnLoad]
public static partial class ArdyRoomStreamDelayRegression
{
    private const string Key = "NeEEvA.ARDY.StreamDelay.";
    private const string RootName = "ARDY delayed stream fixture avatar", FloorName = "ARDY delayed stream fixture floor";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    [Serializable] private sealed class Sample
    {
        public string scenario, session, stage, state;
        public int frame, operation, steps, underruns, recoveries;
        public float realtime, time, buffer, generation, previousConfirmedEnd, rootStep, maximumBoneStep;
        public bool active, playing, returning, owned, recovering;
        public Vector3 root;
    }
    [Serializable] private sealed class Case
    {
        public string name, status = "running", error, initialSession, resumedSession, terminalStatus;
        public int operation, frames, returnFrames, recoveries, underruns, nativeSessions;
        public float started, duration, initialRootDrift, maximumRootStep, maximumBoneStep, stopRootDrift;
        public bool observedDelayedResponse, observedUnderflow, observedRecovery, observedResume, observedPoseReturn, observedExplicitFailure;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, runtimeAssemblySha256, harnessAssemblySha256;
        public string scope = "Existing disposable automated flat-floor fixture, real imported avatar/project Animator, production controller and unmodified real ARDY responses. Loopback proxy only holds selected successful replies; cancellation and other requests pass through immediately. No manually usable scene is created or saved.";
        public string limits = "Tests delayed transport and acknowledged-prefix ownership/fencing. Does not replace the 60s no-fault gait soak, Chat lifecycle test, room terrain checks or human naturalness evaluation.";
        public int checks;
        public List<Case> cases = new List<Case>();
        public List<Sample> samples = new List<Sample>();
        public ArdyRoomStreamDelayProxy.Record[] requests;
    }
    [Serializable] private sealed class Wire
    {
        public string stage, status, session, request, response, error;
        public int operation, steps, recoveries, underruns;
        public ArdyRoomLocomotionController.SegmentEvidence[] segments;
    }
    private static Report report;
    private static Case current;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyRoomLocomotionController controller;
    private static ArdyRoomNavigation navigation;
    private static ArdyLocomotionAvatarReference reference;
    private static ArdyRoomStreamDelayProxy proxy;
    private static IEnumerator routine;
    private static Exception observationError;
    private static Transform[] bones;
    private static Quaternion[] previousBones;
    private static Vector3 previousRoot, caseRoot, stopRoot, userGround, userVelocity;
    private static string previousSession;
    private static int previousSteps, lastFrame;
    private static float previousEnd, previousTime, angle, driveAt, controlAt;
    private static bool observe, stopped, previousPlaying;

    static ArdyRoomStreamDelayRegression()
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
            throw new InvalidOperationException("Only run in the disposable unity-naturalness-validation batch project, without -quit.");
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
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-room-stream-delay")));
            SessionState.SetString(Key + "Endpoint", Argument("-ardyRoomEndpoint", "http://127.0.0.1:8093"));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = FloorName;
            floor.transform.position = new Vector3(0, -.1f, 0); floor.transform.localScale = new Vector3(20, .2f, 20);
            ArdyRoomGeometryPreparation.Prepare(floor.transform, false);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab); root.name = RootName;
            root.transform.localScale = Vector3.one * .9f;
            root.transform.SetPositionAndRotation(new Vector3(0, -imported.groundY * .9f, 0), Quaternion.identity);
            root.GetComponent<Vrm10Instance>().enabled = false;
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            { a.enabled = false; a.runtimeAnimatorController = null; a.applyRootMotion = false; a.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 270);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion, runtimeAssemblySha256 = Hash(typeof(ArdyLocomotionPlayer).Assembly.Location),
            harnessAssemblySha256 = Hash(typeof(ArdyRoomStreamDelayRegression).Assembly.Location) };
        avatar = GameObject.Find(RootName).GetComponent<Vrm10Instance>(); animator = avatar.GetComponent<Animator>();
        string asset = SessionState.GetString(Key + "Avatar", "");
        reference = ArdyLocomotionAvatarReference.FromImportedPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(asset).GetComponent<Vrm10Instance>(), asset);
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Check(animator.runtimeAnimatorController != null && !avatar.enabled, "Real project Animator and disabled VRM required.");
        animator.enabled = true; animator.SetInteger("state", 0);
        var host = new GameObject("ARDY delay regression controller");
        navigation = host.AddComponent<ArdyRoomNavigation>();
        navigation.AgentHeight = Mathf.Max(1.1f, (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * .9f + .18f);
        Check(navigation.Build(GameObject.Find(FloorName).transform, avatar.transform, out string error), error);
        proxy = new ArdyRoomStreamDelayProxy(SessionState.GetString(Key + "Endpoint", ""));
        controller = host.AddComponent<ArdyRoomLocomotionController>(); controller.Endpoint = proxy.Endpoint;
        controller.Configure(avatar, reference, navigation);
        bones = Enumerable.Range(0, (int)HumanBodyBones.LastBone).Select(i => avatar.Humanoid.GetBoneTransform((HumanBodyBones)i)).Where(b => b != null).ToArray();
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        bool qualityOnly=Environment.GetCommandLineArgs().Contains("-ardyQualityRecoveryOnly");
        if(qualityOnly) {
            report.scope="Production controller, actual native locomotion replies and real imported avatar. Explicit proxy-injected 422 quality diagnostics test recovery, exhaustion and cancellation; rejected synthetic diagnostics never contain a pose.";
            report.limits="Injected faults do not reproduce the original missing quality measurements or prove naturalness. Accepted motion remains native service output.";
        }
        bool controlOnly=Environment.GetCommandLineArgs().Contains("-ardyControlRecoveryOnly");
        if(controlOnly) {
            report.scope="Real native ARDY motion and imported avatar. Controlled target becomes 2 cm beyond the committed future seam while prior control continuity is cleared. Production planner must reject braking, persist diagnostics and recover with fresh rendered history.";
            report.limits="Mechanism replay; original failed room trajectory was not retained. No native pose is synthesized by the harness and no kinematic or geometry threshold is relaxed.";
        }
        routine = Environment.GetCommandLineArgs().Contains("-ardyFollowHandoffOnly") ? FollowHandoffScenarios() :
            controlOnly ? ControlScenarios() : qualityOnly ? QualityScenarios() : Scenarios(); lastFrame = -1; observationError = null; InstallObserver(true);
    }

    private static IEnumerator QualityScenarios()
    {
        report.checks+=ArdyRoomStreamDiagnosticsRegression.RunServiceErrorChecks();
        foreach(string mode in new[]{"once","exhaust","cancel"})
        {
            var wait=Reset();while(wait.MoveNext())yield return wait.Current;
            BeginCase("quality-"+mode,3,0,mode=="exhaust");
            proxy.ConfigureQuality(current.name,3,mode=="exhaust");
            BeginStream();wait=WaitForMotion(20);while(wait.MoveNext())yield return wait.Current;
            current.initialSession=controller.StreamSessionId;current.operation=controller.OperationRevision;
            float deadline=Time.realtimeSinceStartup+20;
            while(!controller.StreamIsRecovering) {
                Check(Time.realtimeSinceStartup<deadline && !Failed(),"Quality failure did not start finite recovery: "+controller.Status);
                Drive();yield return null;
            }
            Check(controller.StreamQualityRecoveryCount==1 && controller.StreamRecoveryCount==1 && controller.StreamUnderrunCount==0,
                "Quality recovery must be distinct from supply underflow and limited to one parent-owned attempt.");
            Check(Requests().Any(r=>r.injectedQualityFailure && r.status==422),"Expected explicit quality fault missing.");
            Check(File.Exists(controller.LastQualityFailureEvidencePath),"Full quality evidence was not persisted before cancellation.");
            var evidence=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(controller.LastQualityFailureEvidencePath));
            Check((string)evidence["sessionId"]==current.initialSession && (string)evidence.SelectToken("request.requestId")== (string)evidence["requestId"] &&
                (int)evidence.SelectToken("error.details.timings.qualityAttempts")==3 && ((Newtonsoft.Json.Linq.JArray)evidence["renderedPoseHistory"]).Count>0,
                "Failure identity, all candidate reasons or actual rendered pose history was lost.");
            string output=SessionState.GetString(Key+"Output","");Directory.CreateDirectory(output);
            File.Copy(controller.LastQualityFailureEvidencePath,Path.Combine(output,current.name+"-failure.json"),true);
            current.observedRecovery=true;
            if(mode=="cancel") { wait=StopAndHold(2);while(wait.MoveNext())yield return wait.Current; }
            else if(mode=="once") {
                wait=WaitForResumedMotion(25);while(wait.MoveNext())yield return wait.Current;
                Check(controller.StreamQualityRecoveryCount==1 && current.initialSession!=controller.StreamSessionId,"Recovery replayed the rejected native session.");
                wait=RunHealthyFor(2);while(wait.MoveNext())yield return wait.Current;
                wait=StopAndHold(1);while(wait.MoveNext())yield return wait.Current;
            } else {
                deadline=Time.realtimeSinceStartup+30;
                while(controller.IsContinuousFollowActive || controller.Player.HasPoseOwnership) {
                    Check(Time.realtimeSinceStartup<deadline && controller.OperationRevision==current.operation && controller.StreamQualityRecoveryCount<=1,
                        "Repeated quality failure escaped the fixed operation budget.");Drive();yield return null;
                }
                Check(Failed() && controller.StreamQualityRecoveryCount==1 && Requests().Count(r=>r.injectedQualityFailure && r.status==422)==2,
                    "Second quality rejection did not terminate after one recovery.");
                current.observedExplicitFailure=true;current.terminalStatus=controller.Status;
                wait=HoldInactive(2);while(wait.MoveNext())yield return wait.Current;
            }
            EndCase();
        }
    }

    private static IEnumerator Scenarios()
    {
        var wait = Reset(); while (wait.MoveNext()) yield return wait.Current;
        BeginCase("delayed-second-prefix-must-not-start-prematurely", 1, 900, false);
        BeginStream();
        float deadline = Time.realtimeSinceStartup + 25;
        while (!Requests().Any(r => r.step == 1 && r.delayMilliseconds == 900 && r.finished))
        {
            Check(Time.realtimeSinceStartup < deadline && !Failed(), "Second initial request never completed: " + controller.Status);
            Check(!controller.Player.IsPlaying && Horizontal(avatar.transform.position - caseRoot).magnitude < .0001f,
                "The avatar began walking before its deliberately late second initial prefix was delivered.");
            Drive(); yield return null;
        }
        Check(Requests().Any(r => r.step == 1 && r.status == 200 && r.delivered), "Delayed second initial prefix was not a real successful ARDY reply.");
        current.observedDelayedResponse = true;
        wait = WaitForMotion(15); while (wait.MoveNext()) yield return wait.Current;
        Check(controller.StreamStepCount >= 2 && controller.StreamUnderrunCount == 0 && controller.StreamRecoveryCount == 0,
            "Startup did not prebuffer real adopted prefixes, or incorrectly exhausted its recovery budget before walking.");
        wait = RunHealthyFor(2); while (wait.MoveNext()) yield return wait.Current;
        wait = StopAndHold(1); while (wait.MoveNext()) yield return wait.Current; EndCase();

        wait = Reset(); while (wait.MoveNext()) yield return wait.Current;
        BeginCase("single-late-prefix-recovers-same-operation", 3, 2000, false);
        BeginStream(); wait = WaitForMotion(20); while (wait.MoveNext()) yield return wait.Current;
        current.initialSession = controller.StreamSessionId; current.operation = controller.OperationRevision;
        wait = WaitForRecovery(20); while (wait.MoveNext()) yield return wait.Current;
        wait = WaitForResumedMotion(25); while (wait.MoveNext()) yield return wait.Current;
        Check(controller.StreamRecoveryCount == 1, "A single bounded late reply caused more than one automatic recovery.");
        Check(current.returnFrames > 1, "Underflow recovery omitted a multi-frame Animator transition.");
        wait = RunHealthyFor(3); while (wait.MoveNext()) yield return wait.Current;
        wait = StopAndHold(1); while (wait.MoveNext()) yield return wait.Current; EndCase();

        wait = Reset(); while (wait.MoveNext()) yield return wait.Current;
        BeginCase("persistent-underflow-stops-at-recovery-limit", 3, 2000, true);
        BeginStream(); current.operation = controller.OperationRevision;
        deadline = Time.realtimeSinceStartup + 65;
        while (controller.IsContinuousFollowActive || controller.Player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup < deadline, "Persistent late supply did not reach a bounded terminal failure.");
            Check(controller.OperationRevision == current.operation, "Automatic retries changed the upper-level operation.");
            Check(controller.StreamRecoveryCount <= 3, "Automatic recovery exceeded its configured limit of three.");
            Drive(); yield return null;
        }
        current.observedExplicitFailure = Failed(); current.terminalStatus = controller.Status;
        Check(current.observedExplicitFailure && controller.StreamRecoveryCount == 3 && controller.StreamUnderrunCount >= 4,
            "Persistent supply failure was not explicitly terminated after three recovery attempts: " + controller.Status);
        Check(Requests().Count(r => r.delayMilliseconds > 0 && r.status == 200) >= 4,
            "The repeated failure case did not inject four actual successful but late replies.");
        wait = HoldInactive(2.3f); while (wait.MoveNext()) yield return wait.Current; EndCase();

        wait = Reset(); while (wait.MoveNext()) yield return wait.Current;
        BeginCase("stop-during-recovery-fences-late-results", 3, 2000, false);
        BeginStream(); wait = WaitForMotion(20); while (wait.MoveNext()) yield return wait.Current;
        current.initialSession = controller.StreamSessionId; current.operation = controller.OperationRevision;
        wait = WaitForRecovery(20); while (wait.MoveNext()) yield return wait.Current;
        Check(controller.StreamIsRecovering, "Stop injection missed the recovery interval.");
        wait = StopAndHold(3); while (wait.MoveNext()) yield return wait.Current;
        Check(Requests().Any(r => r.delayMilliseconds == 2000 && r.finished), "Late response was not observed through completion/abort after Stop.");
        EndCase();
    }

    private static IEnumerator Reset()
    {
        observe = false;
        if (controller.IsContinuousFollowActive || controller.Player.HasPoseOwnership) controller.Cancel();
        float deadline = Time.realtimeSinceStartup + 3;
        while (controller.Player.HasPoseOwnership)
        { Check(Time.realtimeSinceStartup < deadline, "Previous case did not release ownership."); yield return null; }
        avatar.transform.SetPositionAndRotation(new Vector3(0, -reference.groundY * .9f, 0), Quaternion.identity);
        Physics.SyncTransforms();
        // Reset between independent tests, then capture a fresh rendered history. This
        // repositioning never occurs inside a running stream or counts as root progress.
        float until = Time.realtimeSinceStartup + 1.1f;
        while (Time.realtimeSinceStartup < until) yield return null;
    }
    private static void BeginCase(string name, int delayedStep, int milliseconds, bool everySession)
    {
        current = new Case { name = name, started = Time.realtimeSinceStartup }; report.cases.Add(current);
        proxy.Configure(name, delayedStep, milliseconds, everySession);
        caseRoot = avatar.transform.position; previousRoot = caseRoot; previousBones = null;
        previousTime = 0; previousSession = null; previousPlaying = false; stopped = false;
        angle = 0; driveAt = Time.realtimeSinceStartup; controlAt = 0; observe = true;
        SetUser();
    }
    private static void BeginStream()
    {
        controller.BeginContinuousFollow(Plan(), userGround, userGround + Vector3.up * 1.6f, userVelocity, Speed(), true);
        current.operation = controller.OperationRevision;
    }
    private static void SetUser()
    {
        userGround = new Vector3(4.5f * Mathf.Sin(angle), 0, -1f + 4.5f * Mathf.Cos(angle));
        userVelocity = new Vector3(Mathf.Cos(angle), 0, -Mathf.Sin(angle)) * .35f;
    }
    private static void Drive()
    {
        float now = Time.realtimeSinceStartup; angle += .35f * (now - driveAt) / 4.5f; driveAt = now; SetUser();
        if (controller.IsContinuousFollowActive && now >= controlAt)
        {
            controlAt = now + .2f;
            Check(controller.TryUpdateContinuousFollow(Plan(), userGround, userGround + Vector3.up * 1.6f, userVelocity, Speed(), true, out string error), error);
        }
    }
    private static float Speed() => Mathf.Clamp(.35f + Mathf.Max(0, Horizontal(userGround - Ground()).magnitude - 1.25f) * .25f, .35f, .65f);
    private static ArdyRoomRoute Plan()
    {
        var direction = Horizontal(userGround - Ground()).normalized;
        Check(navigation.TryPlan(Ground(), userGround - direction * 1.1f, out var route, out string error), "Fixture planning failed: " + error);
        return route;
    }
    private static IEnumerator WaitForMotion(float seconds)
    {
        float deadline = Time.realtimeSinceStartup + seconds; Vector3 start = avatar.transform.position;
        while (!controller.Player.IsPlaying || Horizontal(avatar.transform.position - start).magnitude < .06f)
        {
            Check(Time.realtimeSinceStartup < deadline && !Failed() && controller.IsContinuousFollowActive,
                "Actual motion failed to start: " + controller.Status);
            Drive(); yield return null;
        }
    }
    private static IEnumerator WaitForRecovery(float seconds)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!controller.StreamIsRecovering)
        {
            Check(Time.realtimeSinceStartup < deadline && !Failed() && controller.IsContinuousFollowActive,
                "Late prefix did not enter bounded recovery: " + controller.Status);
            Drive(); yield return null;
        }
        Check(controller.StreamUnderrunCount >= 1 && controller.StreamRecoveryCount >= 1,
            "Recovery was entered without recording the injected underflow.");
        current.observedUnderflow = current.observedRecovery = true;
        current.observedDelayedResponse = Requests().Any(r => r.delayMilliseconds == 2000 && r.status == 200);
        Check(current.observedDelayedResponse, "Recovery happened without the intended successful delayed reply.");
    }
    private static IEnumerator WaitForResumedMotion(float seconds)
    {
        float deadline = Time.realtimeSinceStartup + seconds; Vector3 held = avatar.transform.position;
        while (controller.StreamSessionId == current.initialSession || !controller.Player.IsPlaying ||
            controller.StreamIsRecovering || Horizontal(avatar.transform.position - held).magnitude < .08f)
        {
            Check(Time.realtimeSinceStartup < deadline && !Failed() && controller.IsContinuousFollowActive,
                "Follow failed to resume after a single late reply: " + controller.Status);
            Check(controller.OperationRevision == current.operation, "Recovery replaced the upper-level operation.");
            Drive(); yield return null;
        }
        Check(!string.IsNullOrEmpty(controller.StreamSessionId), "Resumed motion lacks a fresh native session.");
        current.resumedSession = controller.StreamSessionId; current.observedResume = true;
    }
    private static IEnumerator RunHealthyFor(float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds; int count = controller.StreamRecoveryCount;
        while (Time.realtimeSinceStartup < until)
        {
            Check(controller.IsContinuousFollowActive && !Failed() && controller.StreamRecoveryCount == count && controller.Player.IsPlaying,
                "No-fault observation lost continuous movement: " + controller.Status);
            Drive(); yield return null;
        }
    }
    private static IEnumerator StopAndHold(float seconds)
    {
        var before = bones.Select(b => b.localRotation).ToArray(); stopRoot = avatar.transform.position; stopped = true;
        controller.Cancel();
        Check(!controller.IsContinuousFollowActive && !controller.Player.IsPlaying, "Stop did not immediately revoke locomotion.");
        for (int i = 0; i < bones.Length; i++) Check(Quaternion.Angle(before[i], bones[i].localRotation) < .1f, "Stop snapped a rendered bone synchronously.");
        float deadline = Time.realtimeSinceStartup + 2;
        while (controller.Player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup < deadline && Vector3.Distance(stopRoot, avatar.transform.position) < .0001f,
                "Stop did not hold root during a bounded pose return.");
            yield return null;
        }
        Check(animator.enabled, "Stop did not restore the project Animator.");
        var hold = HoldInactive(seconds); while (hold.MoveNext()) yield return hold.Current;
    }
    private static IEnumerator HoldInactive(float seconds)
    {
        Vector3 held = avatar.transform.position; long playback = controller.Player.PlaybackRevision;
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            Check(!controller.IsContinuousFollowActive && !controller.Player.HasPoseOwnership && !controller.Player.IsPlaying &&
                controller.Player.PlaybackRevision == playback && Vector3.Distance(held, avatar.transform.position) < .0001f,
                "A late response or moving target restarted a cancelled/terminal stream.");
            Drive(); yield return null;
        }
    }

    private static void Observe()
    {
        if (!observe || current == null || observationError != null) return;
        try
        {
            float now = Time.realtimeSinceStartup, dt = previousTime > 0 ? now - previousTime : 0;
            Vector3 root = avatar.transform.position; float rootStep = dt > 0 ? Vector3.Distance(root, previousRoot) : 0;
            var rotations = bones.Select(b => b.localRotation).ToArray(); float boneStep = 0;
            if (previousBones != null) for (int i = 0; i < rotations.Length; i++) boneStep = Mathf.Max(boneStep, Quaternion.Angle(previousBones[i], rotations[i]));
            Check(rootStep < Mathf.Max(.15f, dt * 2.5f), "Rendered root teleported while buffering/recovering.");
            if (previousPlaying && controller.Player.IsPlaying && previousSession == controller.StreamSessionId && previousSteps == controller.StreamStepCount)
                Check(controller.Player.TimeSeconds <= previousEnd + .002f, "A rendered sample exceeded the previously confirmed motion prefix.");
            if (controller.Player.IsReturningToAnimation) { current.returnFrames++; current.observedPoseReturn = true; }
            current.frames++; current.maximumRootStep = Mathf.Max(current.maximumRootStep, rootStep); current.maximumBoneStep = Mathf.Max(current.maximumBoneStep, boneStep);
            if (stopped) current.stopRootDrift = Mathf.Max(current.stopRootDrift, Vector3.Distance(stopRoot, root));
            if (controller.StreamStepCount < 2) current.initialRootDrift = Mathf.Max(current.initialRootDrift, Vector3.Distance(caseRoot, root));
            Check(report.samples.Count < 16000, "Delay fixture exceeded bounded observation storage.");
            report.samples.Add(new Sample { scenario = current.name, session = controller.StreamSessionId, frame = Time.frameCount,
                operation = controller.OperationRevision, steps = controller.StreamStepCount, underruns = controller.StreamUnderrunCount,
                recoveries = controller.StreamRecoveryCount, stage = controller.Stage, state = controller.ExecutionState.ToString(),
                realtime = now, time = controller.Player.TimeSeconds, buffer = controller.StreamBufferSeconds,
                generation = controller.StreamGenerationSeconds, previousConfirmedEnd = previousEnd,
                rootStep = rootStep, maximumBoneStep = boneStep, root = root, active = controller.IsContinuousFollowActive,
                playing = controller.Player.IsPlaying, returning = controller.Player.IsReturningToAnimation, owned = controller.Player.HasPoseOwnership,
                recovering = controller.StreamIsRecovering });
            previousEnd = controller.Player.TimeSeconds + controller.StreamBufferSeconds - .05f;
            previousSession = controller.StreamSessionId; previousSteps = controller.StreamStepCount; previousPlaying = controller.Player.IsPlaying;
            previousRoot = root; previousBones = rotations; previousTime = now;
        }
        catch (Exception error) { observationError = error; }
    }
    private static bool Failed() => controller.ExecutionState == ArdyRoomExecutionState.Failed || controller.ExecutionState == ArdyRoomExecutionState.Blocked;
    private static ArdyRoomStreamDelayProxy.Record[] Requests() => proxy.Snapshot().Where(r => r.scenario == current.name).ToArray();
    private static Vector3 Ground() => avatar.transform.position + Vector3.up * reference.groundY * .9f;
    private static Vector3 Horizontal(Vector3 value) { value.y = 0; return value; }
    private static void EndCase()
    {
        current.status = "passed"; CompleteCase(); Save(); current = null; observe = false;
    }
    private static void CompleteCase()
    {
        current.duration = Time.realtimeSinceStartup - current.started;
        current.recoveries = controller.StreamRecoveryCount; current.underruns = controller.StreamUnderrunCount;
        current.nativeSessions = Requests().Where(r => r.step >= 0).Select(r => r.session).Distinct().Count();
    }
    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Delay regression exceeded its 270s bound.")); return; }
        if (!EditorApplication.isPlaying || routine == null || lastFrame == Time.frameCount) return;
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
            var children = item.subSystemList.Where(c => c.type != typeof(ArdyRoomStreamDelayRegression)).ToList();
            for (int i = 0; i < children.Count; i++) { var child = children[i]; Rewrite(ref child); children[i] = child; }
            if (install && item.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                children.Add(new PlayerLoopSystem { type = typeof(ArdyRoomStreamDelayRegression), updateDelegate = Observe });
            item.subSystemList = children.ToArray();
        }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        SessionState.SetBool(Key + "Finished", true); InstallObserver(false); observe = false;
        if (report == null) report = new Report(); report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        if (current != null && error != null) { current.status = "failed"; current.error = error.ToString(); CompleteCase(); }
        Save();
        try { (routine as IDisposable)?.Dispose(); controller?.Cancel(); controller?.Player?.Stop(); proxy?.Dispose(); }
        catch (Exception cleanup) { report.status = "failed"; report.error += "\nCleanup: " + cleanup; Save(); }
        SessionState.SetInt(Key + "Exit", report.status == "passed" ? 0 : 1);
        if (error != null) Debug.LogException(error);
        EditorApplication.isPlaying = false;
    }
    private static void Save()
    {
        string output = SessionState.GetString(Key + "Output", "Logs/ardy-room-stream-delay"); Directory.CreateDirectory(output);
        report.requests = proxy?.Snapshot(); File.WriteAllText(Path.Combine(output, "stream-delay.json"), JsonUtility.ToJson(report, true));
        if (controller == null) return;
        var wire = new Wire { stage = controller.Stage, status = controller.Status, session = controller.StreamSessionId,
            request = controller.LastRequestJson, response = controller.LastResponseJson, error = controller.LastErrorResponseJson,
            operation = controller.OperationRevision, steps = controller.StreamStepCount, recoveries = controller.StreamRecoveryCount,
            underruns = controller.StreamUnderrunCount, segments = controller.Segments.ToArray() };
        File.WriteAllText(Path.Combine(output, (current?.name ?? "final") + "-wire.json"), JsonUtility.ToJson(wire, true));
    }
    private static void FinalizeBatch()
    { int code = SessionState.GetInt(Key + "Exit", 1); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(code); }
    private static void Check(bool value, string reason) { if (report != null) report.checks++; if (!value) throw new InvalidOperationException(reason); }
    private static string Argument(string name, string fallback)
    { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Hash(string path)
    { using (var file = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
}
