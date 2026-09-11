using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Actual HTTP, Coroutine, Animator and player-loop integration; never opens the private chat scene.</summary>
[InitializeOnLoad]
public static class ArdyLivePlayModeRegression
{
    private const string Key = "NeEEvA.ARDY.LiveRegression.";
    private static readonly string Description = Argument("-ardyMotionText", "While standing in place, a person slowly extends both forearms forward with palms facing upward, as if calmly explaining something.");
    private static readonly ArdyMotionGoal IntentGoal = new ArdyMotionGoal {
        leftGoal = Argument("-ardyLeftGoal", "any"), rightGoal = Argument("-ardyRightGoal", "any") };
    private static readonly string Output = Path.GetFullPath(Argument("-ardyLiveReport", Path.Combine(Application.dataPath, "../Logs/ardy-live-playmode.json")));
    [Serializable] private sealed class Report
    {
        public string status = "running";
        public string method = "Real Unity PlayMode, real Animator, live HTTP Qwen feature + MLP + ARDY, real UnityWebRequest and motion controller. No manual Tick or Animator.Update.";
        public string limitation = "Isolated real NEVA avatar with original Animator; no private scene, live microphone, TTS or subjective naturalness rating. Dialogue parsing has its separate real ChatSample regression.";
        public string unityVersion, error, description = Description;
        public ArdyMotionGoal requestedNecessaryGoal;
        public ArdyMotionExecutionFeedback completedIntentFeedback;
        public int checks, sampledFrames, receivedChunks, timelineFrames;
        public float firstWindowSeconds, allWindowsSeconds, finalReturnErrorDegrees, cancelledReturnErrorDegrees;
        public float initialWristBelowShoulderMetres, dynamicUpperBodyExcursionDegrees;
        public bool historyCapturedFromAnimator, lateResponseRejected, preResponseCancelStayedIdle;
        public string diagnosticTracePath;
        public int diagnosticRequests, diagnosticResponses, initialHistoryFrames;
        public string serviceUrl;
        public int initialConditioningFrames;
        public bool motionContextBoundIdle, motionContextGenerating, motionContextPlaying;
        public bool motionContextCompletedIdle, motionContextCancelledBeforeResponse;
        public bool motionContextCancelledWhilePlaying, motionContextCancelledIdle, motionContextRebindCleared;
        public List<ContextObservation> motionContextFacts = new List<ContextObservation>();
        public ArdyMotionTimings lastWindowTimings;
    }
    [Serializable] private sealed class ContextObservation { public string checkpoint, json; }
    [Serializable] private sealed class ContextFact
    {
        public string phase, name, description, lastRequestedName, lastRequestedDescription, lastEndReason;
        public string endPolicy, observation;
        public bool previousRequestedPoseHeld;
    }
    private static Report report;
    [Serializable] private sealed class TraceEntry { public string kind, json; public long httpStatus; }
    [Serializable] private sealed class TraceDocument { public string finishReason; public TraceEntry[] entries; }
    private static ArdyLiveMotionController controller;
    private static ArdyMotionPlayer player;
    private static Vrm10Instance avatar, control;
    private static Animator animator, controlAnimator;
    private static int stage, lastFrame;
    private static float stageAt;
    private static long cancelledRevision;
    private static readonly Dictionary<HumanBodyBones, Quaternion> startingPose = new Dictionary<HumanBodyBones, Quaternion>();
    private static readonly HumanBodyBones[] Compared = {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
        HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
    };

    static ArdyLivePlayModeRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Initialize();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Finish();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && !EditorApplication.isPlaying && SessionState.GetBool(Key + "Finished", false)) Finish();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch-only validation entry point.");
        // This harness replaces only the disposable validation project's temporary scene.
        if (Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run in the isolated unity-naturalness-validation project.");
        try
        {
            string url = Argument("-ardyServiceUrl", "http://127.0.0.1:8093");
            int conditioning = int.Parse(Argument("-ardyConditioningFrames", "16"));
            ArdyLiveProtocol.ValidateConditioningFrames(conditioning);
            IntentGoal.Validate();
            SessionState.SetString(Key + "ServiceUrl", url);
            SessionState.SetInt(Key + "ConditioningFrames", conditioning);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NEVA.vrm");
            var animation = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/AIChatTookit/Animation/Animator Controller.controller");
            if (prefab == null || animation == null) throw new InvalidOperationException("Actual avatar/Animator missing.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (int i = 0; i < 2; i++)
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                root.name = "ARDY-Live-Test-" + i;
                root.transform.SetPositionAndRotation(new Vector3(i * 4f, 0, 0), Quaternion.Euler(0, 37, 0));
                var vrm = root.GetComponent<Vrm10Instance>();
                vrm.enabled = false;
                vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
                var anim = root.GetComponent<Animator>();
                anim.runtimeAnimatorController = animation;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.enabled = true;
            }
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 100);
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion,
            serviceUrl = SessionState.GetString(Key + "ServiceUrl", "http://127.0.0.1:8093"),
            initialConditioningFrames = SessionState.GetInt(Key + "ConditioningFrames", 16) };
        avatar = GameObject.Find("ARDY-Live-Test-0").GetComponent<Vrm10Instance>();
        control = GameObject.Find("ARDY-Live-Test-1").GetComponent<Vrm10Instance>();
        animator = avatar.GetComponent<Animator>();
        controlAnimator = control.GetComponent<Animator>();
        stage = 0;
        lastFrame = -1;
        stageAt = Time.realtimeSinceStartup;
    }

    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Fail(new TimeoutException("Live PlayMode test exceeded deadline.")); return; }
        if (!EditorApplication.isPlaying || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        report.sampledFrames++;
        float elapsed = Time.realtimeSinceStartup - stageAt;
        try
        {
            if (stage == 0)
            {
                if (elapsed < 0.7f) return;
                if (controller == null)
                {
                    report.initialWristBelowShoulderMetres = Bone(avatar, HumanBodyBones.LeftUpperArm).position.y - Bone(avatar, HumanBodyBones.LeftHand).position.y;
                    Check(report.initialWristBelowShoulderMetres > 0.15f, "The real Animator did not reach idle before binding.");
                    controller = avatar.gameObject.AddComponent<ArdyLiveMotionController>();
                    controller.serviceUrl = report.serviceUrl;
                    controller.initialConditioningFrames = report.initialConditioningFrames;
                    controller.Bind(avatar);
                    player = avatar.GetComponent<ArdyMotionPlayer>();
                    CheckMotionContext("bound-idle", "idle-Animator", "", "", "", "");
                    report.motionContextBoundIdle = true;
                    var history = player.CaptureUpperBodyHistoryFrame();
                    Check(Quaternion.Angle(history.globalRotations[14], Quaternion.identity) > 20, "Initial history is a T-pose rather than the current Animator pose.");
                    report.historyCapturedFromAnimator = true;
                    foreach (var bone in Compared) if (Bone(avatar, bone) != null) startingPose[bone] = Bone(avatar, bone).localRotation;
                    return;
                }
                // Exercise a full measured 16-frame history, as after connecting and then talking.
                if (elapsed < 1.6f) return;
                report.requestedNecessaryGoal = IntentGoal.Copy();
                controller.MotionFeedback += fact => {
                    if (fact.actionId == "intent-live-original" && (fact.status == "completed" || fact.status == "goal-unmet"))
                        report.completedIntentFeedback = fact.Copy();
                };
                controller.RequestMotion("generate", Description, 1, "intent-live-original", IntentGoal);
                Check(controller.DescribeActiveMotion().Contains("generate"), "Active motion is absent from dialogue context facts.");
                CheckMotionContext("generating-first-request", "generating-or-buffering", "generate", "generate", Description, "");
                report.motionContextGenerating = true;
                Next(1);
            }
            else if (stage == 1)
            {
                Check((bool)Field(typeof(ArdyLiveMotionController), "liveRequest").GetValue(controller), controller.Status);
                Check(player.StreamFailure == null, "Real streaming underrun: " + player.StreamFailure);
                if (controller.ReceivedChunks > 0 && report.firstWindowSeconds == 0) report.firstWindowSeconds = elapsed;
                foreach (var id in new[] { HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.Spine })
                    report.dynamicUpperBodyExcursionDegrees = Mathf.Max(report.dynamicUpperBodyExcursionDegrees, Quaternion.Angle(startingPose[id], Bone(avatar, id).localRotation));
                if (controller.ReceivedChunks == 3 && report.allWindowsSeconds == 0)
                {
                    report.allWindowsSeconds = elapsed;
                    report.receivedChunks = controller.ReceivedChunks;
                    report.lastWindowTimings = controller.LastTimings;
                    var clip = (ArdyMotionClip)Field(typeof(ArdyMotionPlayer), "clip").GetValue(player);
                    report.timelineFrames = clip.frames.Length;
                    Check(report.timelineFrames == 120, "HTTP windows did not append to exactly 120 new frames.");
                }
                if (controller.ReceivedChunks == 3 && player.IsPlaying && !report.motionContextPlaying)
                {
                    CheckMotionContext("playing-complete-buffer", "playing", "generate", "generate", Description, "");
                    report.motionContextPlaying = true;
                }
                if (elapsed > 25 && controller.ReceivedChunks < 3) throw new Exception(controller.Status);
                if (report.receivedChunks == 3 && !player.IsPlaying)
                {
                    Check(report.dynamicUpperBodyExcursionDegrees > 3, "Generated motion never altered the real upper body.");
                    Check(player.StreamFailure == null, "Completed stream reported failure.");
                    report.finalReturnErrorDegrees = CompareWithAnimator();
                    Check(report.finalReturnErrorDegrees < 0.12f, "Completed action did not restore the current Animator.");
                    Check(string.IsNullOrEmpty(controller.DescribeActiveMotion()), "Finished motion remains in active dialogue facts.");
                    CheckTrace();
                    CheckMotionContext("naturally-completed-idle", "idle-Animator", "", "generate", Description,
                        "completed-and-returned-to-Animator");
                    Check(report.motionContextPlaying, "No actual playback phase was observed in motion context facts.");
                    report.motionContextCompletedIdle = true;
                    Check(report.completedIntentFeedback != null, "Completed live action lost its identity/observation feedback.");
                    Check(report.completedIntentFeedback.observation != null && report.completedIntentFeedback.observation.sampleCount > 0,
                        "Live action has no post-VRM actual-pose samples.");
                    if (IntentGoal.HasTargets && report.completedIntentFeedback.observation.status == "not-reached")
                        Check(report.completedIntentFeedback.status == "goal-unmet" && report.completedIntentFeedback.canRepair,
                            "Measured failure was labelled successful or lost its bounded correction eligibility.");
                    // Cancel before any HTTP response. A new revision must not later start old motion.
                    controller.RequestMotion("generate", Description, 2);
                    cancelledRevision = controller.Revision;
                    controller.Cancel("regression-before-first-response");
                    Check(controller.Revision > cancelledRevision, "Cancellation did not advance the revision.");
                    CheckCancellationTrace("regression-before-first-response");
                    CheckMotionContext("cancelled-before-response", "idle-Animator", "", "generate", Description,
                        "regression-before-first-response");
                    report.motionContextCancelledBeforeResponse = true;
                    Next(2);
                }
            }
            else if (stage == 2)
            {
                Check(!player.IsPlaying && controller.ReceivedChunks == 0, "A canceled first HTTP response resurrected motion.");
                if (elapsed < 1) return;
                CheckMotionContext("cancelled-before-response-still-idle", "idle-Animator", "", "generate", Description,
                    "regression-before-first-response");
                report.preResponseCancelStayedIdle = true;
                controller.RequestMotion("generate", Description, 3);
                Next(3);
            }
            else if (stage == 3)
            {
                if (elapsed > 25) throw new Exception("Second live request failed: " + controller.Status);
                if (controller.ReceivedChunks == 0 || elapsed < 0.8f) return;
                Check(player.IsPlaying, "No actual motion to interrupt.");
                cancelledRevision = controller.Revision;
                controller.Cancel("regression-interrupt-playing");
                CheckCancellationTrace("regression-interrupt-playing");
                CheckMotionContext("cancelled-playback-returning", "returning-to-Animator", "", "generate", Description,
                    "regression-interrupt-playing");
                report.motionContextCancelledWhilePlaying = true;
                Check(!player.CanAppendStream, "Interrupted stream still accepts continuation.");
                // Even a late result whose contents are malformed must be rejected before interpretation.
                bool accepted = (bool)typeof(ArdyLiveMotionController).GetMethod("ApplyResponse", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, new object[] { null, null, cancelledRevision });
                Check(!accepted, "Old-generation response reached the player after cancel.");
                report.lateResponseRejected = true;
                Next(4);
            }
            else if (stage == 4)
            {
                if (elapsed < 0.5f) return;
                Check(!player.IsPlaying, "Interrupted action failed to return to idle.");
                report.cancelledReturnErrorDegrees = CompareWithAnimator();
                Check(report.cancelledReturnErrorDegrees < 0.12f, "Cancel did not restore current Animator pose.");
                CheckMotionContext("cancelled-playback-idle", "idle-Animator", "", "generate", Description,
                    "regression-interrupt-playing");
                report.motionContextCancelledIdle = true;
                // Accepted controlled head gesture must still be available immediately after a live stream.
                controller.RequestMotion("nod", "", 4);
                Check(player.IsPlaying, "Basic accepted nod unavailable after dynamic action.");
                CheckMotionContext("basic-nod-requested", "playing", "nod", "nod", "", "");
                controller.Cancel("regression-cleanup");
                controller.Bind(avatar);
                Check(controller.IsBound && !player.IsPlaying, "Rebind did not restore a bound idle controller.");
                CheckMotionContext("rebound-clears-previous-request", "idle-Animator", "", "", "", "");
                Check(string.IsNullOrEmpty(controller.DescribeActiveMotion()), "Rebind retained an active motion fact.");
                report.motionContextRebindCleared = true;
                report.status = "passed";
                SaveAndExitPlay();
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static float CompareWithAnimator()
    {
        Check(Mathf.Abs(animator.GetCurrentAnimatorStateInfo(0).normalizedTime - controlAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime) < 0.001f,
            "Control Animator lost time synchronization.");
        float error = 0;
        foreach (var id in Compared)
        {
            var a = Bone(avatar, id); var b = Bone(control, id);
            if (a != null && b != null) error = Mathf.Max(error, Quaternion.Angle(a.localRotation, b.localRotation));
        }
        return error;
    }
    private static void CheckTrace()
    {
        report.diagnosticTracePath = controller.LastDiagnosticPath;
        Check(File.Exists(report.diagnosticTracePath), "Actual HTTP motion diagnostic trace is missing.");
        var trace = JsonUtility.FromJson<TraceDocument>(File.ReadAllText(report.diagnosticTracePath));
        Check(trace.finishReason == "completed", "Natural completion was not recorded in motion diagnostics.");
        ArdyGenerateRequest pending = null;
        foreach (var entry in trace.entries)
        {
            if (entry.kind == "request")
            {
                pending = JsonUtility.FromJson<ArdyGenerateRequest>(entry.json);
                report.diagnosticRequests++;
                if (pending.chunkIndex == 0)
                {
                    report.initialHistoryFrames = pending.initialHistory.frames.Length;
                    Check(report.initialHistoryFrames == 16, "Trace did not capture the full actual sampled history.");
                    Check(pending.initialHistory.conditioningFrames == report.initialConditioningFrames,
                        "Trace lost the explicit conditioning length independently of captured frame count.");
                }
                else pending.initialHistory = null; // Mirror the actual absence, not Unity's inline default object.
            }
            if (entry.kind == "response")
            {
                Check(pending != null && entry.httpStatus == 200, "Trace response is not paired to its real HTTP request.");
                ArdyLiveProtocol.ValidateResponse(JsonUtility.FromJson<ArdyGenerateResponse>(entry.json), pending);
                report.diagnosticResponses++;
                pending = null;
            }
        }
        Check(report.diagnosticRequests == 3 && report.diagnosticResponses == 3, "Trace cannot replay all three HTTP windows.");
        Check(Directory.GetFiles(Path.GetDirectoryName(report.diagnosticTracePath), "ardy-motion-*.json").Length <= 8,
            "Motion diagnostic retention exceeded eight traces.");
    }
    private static void CheckCancellationTrace(string reason)
    {
        var trace = JsonUtility.FromJson<TraceDocument>(File.ReadAllText(controller.LastDiagnosticPath));
        Check(trace.finishReason == reason, "Cancelled motion diagnostic lost its actual cancellation reason.");
        Check(string.IsNullOrEmpty(controller.DescribeActiveMotion()), "Cancelled motion remains in active dialogue facts.");
    }
    private static void CheckMotionContext(string checkpoint, string phase, string activeName,
        string lastName, string lastDescription, string endReason)
    {
        string json = controller.DescribeMotionContext();
        Check(!string.IsNullOrWhiteSpace(json), checkpoint + ": bound controller omitted lifecycle facts.");
        var context = JsonUtility.FromJson<ContextFact>(json);
        Check(context != null && context.phase == phase, checkpoint + ": wrong lifecycle phase: " + json);
        Check(context.endPolicy == "return-to-current-Animator", checkpoint + ": completion policy changed.");
        Check(!context.previousRequestedPoseHeld && json.Contains("\"previousRequestedPoseHeld\""),
            checkpoint + ": context claims or omits whether the previous requested pose is held.");
        Check(context.name == activeName && context.description == (activeName == "generate" ? Description : ""),
            checkpoint + ": active intent fields do not match the actual lifecycle.");
        Check(context.lastRequestedName == lastName && context.lastRequestedDescription == lastDescription,
            checkpoint + ": most recent requested action was lost or confused with observed success.");
        Check(context.lastEndReason == endReason, checkpoint + ": terminal reason changed or was cleared.");
        Check(!string.IsNullOrWhiteSpace(context.observation),
            checkpoint + ": lifecycle facts lost their requested-versus-observed qualification.");
        report.motionContextFacts.Add(new ContextObservation { checkpoint = checkpoint, json = json });
    }
    private static Transform Bone(Vrm10Instance vrm, HumanBodyBones bone) => vrm.Humanoid.GetBoneTransform(bone);
    private static string Argument(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + name);
        return args[index + 1];
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    private static void Next(int value) { stage = value; stageAt = Time.realtimeSinceStartup; }
    private static void Check(bool value, string message) { report.checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Fail(Exception exception)
    {
        Debug.LogException(exception);
        if (report == null) report = new Report();
        report.status = "failed"; report.error = exception.ToString();
        if (controller != null) controller.Cancel("regression-failed");
        SaveAndExitPlay();
    }
    private static void SaveAndExitPlay()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        File.WriteAllText(Output, JsonUtility.ToJson(report, true));
        SessionState.SetBool(Key + "Passed", report.status == "passed");
        SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Finish();
    }
    private static void Finish()
    {
        SessionState.SetBool(Key + "Active", false);
        EditorApplication.Exit(SessionState.GetBool(Key + "Passed", false) ? 0 : 1);
    }
}
