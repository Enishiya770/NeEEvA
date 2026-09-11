using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Real PlayMode lifecycle/replay checks. No network, microphone, private scene or model.</summary>
[InitializeOnLoad]
public static class ArdyIntentReplayRegression
{
    private const string Key = "ArdyIntentReplayRegression.";
    [Serializable] private sealed class Report
    {
        public string error, unityVersion;
        public int checks, sampledFrames;
        public bool success;
        public string scope = "Real isolated NEVA/Animator with exact stored clip replay; no network or semantic success claim.";
        public List<string> feedback = new List<string>();
        public List<string> checkpoints = new List<string>();
        public List<string> transportBoundaryCases = new List<string>();
    }
    private static Report report;
    private static ArdyLiveMotionController controller;
    private static Vrm10Instance avatar;
    private static int stage, lastFrame;
    private static double deadline;
    private static string firstArchive;
    private static long replayRevision;
    private static ArdyMotionClip firstPlayerClip;
    private static ArdyMotionClip transportSuccessor;
    private static int transportSuccessorSerial;

    static ArdyIntentReplayRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += value => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (value == PlayModeStateChange.EnteredPlayMode) Initialize();
            if (value == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
    }
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only run in the isolated validation project.");
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NEVA.vrm");
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            root.name = "Intent-Replay-Avatar";
            root.transform.rotation = Quaternion.Euler(0, 37, 0);
            var vrm = root.GetComponent<Vrm10Instance>();
            vrm.enabled = false;
            vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
            var animator = root.GetComponent<Animator>();
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/AIChatTookit/Animation/Animator Controller.controller");
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetBool(Key + "Finished", false);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion };
        avatar = GameObject.Find("Intent-Replay-Avatar").GetComponent<Vrm10Instance>();
        controller = avatar.gameObject.AddComponent<ArdyLiveMotionController>();
        controller.recordMotionDiagnostics = false;
        controller.allowGeneratedMotion = true;
        controller.MotionFeedback += value => report.feedback.Add(JsonUtility.ToJson(value));
        controller.Bind(avatar);
        stage = 0; lastFrame = -1; deadline = EditorApplication.timeSinceStartup + 55;
    }
    private static void Check(bool value, string message)
    {
        report.checks++;
        if (!value) throw new InvalidOperationException(message);
    }
    private static JObject Context()
    {
        string value = controller.DescribeMotionContext();
        report.checkpoints.Add(value);
        return JObject.Parse(value);
    }
    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false) ||
            !EditorApplication.isPlaying || report == null || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount; report.sampledFrames++;
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Replay validation timed out.");
            if (stage == 0)
            {
                Check(string.IsNullOrEmpty(controller.LastReplayableActionId), "New binding invented a replay.");
                bool rejected = false;
                try { controller.RequestReplay("last", 1, "missing"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Missing replay was silently regenerated.");
                Check(controller.CurrentActionId == null, "Rejected replay created an active action.");
                controller.RequestMotion("left-wave", "accepted left wave", 2, "original-wave");
                firstPlayerClip = PlayerClip();
                Check(controller.CurrentActionId == "original-wave", "Action identity did not reach controller.");
                Check(controller.ReceivedChunks == 0, "Preset contacted the generated service.");
                stage = 1;
            }
            else if (stage == 1 && controller.CurrentActionId == null)
            {
                Check(controller.LastReplayableActionId == "original-wave", "Completed clip was not retained.");
                var context = Context();
                Check((string)context["lastExecutionFeedback"]["status"] == "completed", "Completion feedback missing.");
                Check((string)context["lastExecutionFeedback"]["observation"]["status"] == "unassessed", "No goal was falsely scored as semantic success.");
                firstArchive = (string)context["replayableActions"][0]["trajectorySha256"];
                Check(!string.IsNullOrEmpty(firstArchive), "Replay archive has no exact trajectory identity.");
                controller.RequestReplay("last", 3, "repeated-wave");
                var replayClip = PlayerClip();
                Check(!ReferenceEquals(replayClip, firstPlayerClip) && !ReferenceEquals(replayClip.frames, firstPlayerClip.frames),
                    "Replay mutated or reused the previous Player buffer.");
                Check(replayClip.frames.Length == firstPlayerClip.frames.Length && replayClip.fps == firstPlayerClip.fps,
                    "Replay changed trajectory duration.");
                for (int frame = 0; frame < replayClip.frames.Length; frame++)
                    for (int joint = 0; joint < replayClip.frames[frame].globalRotations.Length; joint++)
                    {
                        var a = replayClip.frames[frame].globalRotations[joint];
                        var b = firstPlayerClip.frames[frame].globalRotations[joint];
                        Check(Mathf.Abs(a.x - b.x) < 1e-6f && Mathf.Abs(a.y - b.y) < 1e-6f &&
                            Mathf.Abs(a.z - b.z) < 1e-6f && Mathf.Abs(a.w - b.w) < 1e-6f,
                            "Player is not playing the exact saved source trajectory.");
                    }
                replayRevision = controller.Revision;
                Check(controller.CurrentActionId == "repeated-wave", "Replay did not receive a new execution identity.");
                Check(controller.ReceivedChunks == 0, "Replay generated a new network clip.");
                stage = 2;
            }
            else if (stage == 2 && controller.CurrentActionId == null)
            {
                var context = Context();
                Check(controller.LastReplayableActionId == "repeated-wave", "Replay completion not retained.");
                Check(controller.Revision == replayRevision, "Replay unexpectedly started another request.");
                Check((string)context["lastExecutionFeedback"]["replayOf"] == "original-wave", "Replay lost the referenced action.");
                Check((string)context["replayableActions"][1]["trajectorySha256"] == firstArchive, "Exact stored trajectory changed on replay.");
                controller.RequestMotion("right-wave", "interrupt this", 4, "interrupted-wave");
                controller.Cancel("user-started-speaking");
                Check(controller.LastReplayableActionId == "repeated-wave", "Cancelled partial action replaced completed replay.");
                Check((string)Context()["lastExecutionFeedback"]["status"] == "cancelled", "Cancellation was labelled complete.");
                controller.RequestMotion("right-wave", "external stop", 5, "external-stop");
                avatar.GetComponent<ArdyMotionPlayer>().Stop();
                stage = 3;
            }
            else if (stage == 3 && controller.CurrentActionId == null)
            {
                Check(controller.LastReplayableActionId == "repeated-wave", "External Player.Stop archived a partial action.");
                controller.RequestMotion("right-wave", "external replacement", 6, "external-replacement");
                avatar.GetComponent<ArdyMotionPlayer>().Play(Resources.Load<TextAsset>("ARDY/nod"), ArdyMotionMask.Head);
                stage = 4;
            }
            else if (stage == 4 && controller.CurrentActionId == null)
            {
                Check(controller.LastReplayableActionId == "repeated-wave", "External replacement archived a different action as complete.");
                Check(avatar.GetComponent<ArdyMotionPlayer>().IsPlaying, "Retiring the old action stopped its external successor.");
                CheckResponseBeforeOwnershipLateUpdate(false);
                CheckResponseBeforeOwnershipLateUpdate(true);
                stage = 5;
            }
            else if (stage == 5)
            {
                var player = avatar.GetComponent<ArdyMotionPlayer>();
                Check(player.MotionPlaybackSerial == transportSuccessorSerial && ReferenceEquals(PlayerClip(), transportSuccessor)
                    && player.IsPlaying && !player.MotionPlaybackWasInterrupted,
                    "An old response's deferred cleanup stopped or replaced the external successor on the next actual frame.");
                Check(controller.CurrentActionId == null && controller.LastReplayableActionId == "repeated-wave",
                    "Retired transport response retained action authority or created a replay.");
                controller.Bind(avatar);
                Check(controller.CurrentActionId == null && string.IsNullOrEmpty(controller.LastReplayableActionId), "Rebind retained old action authority.");
                bool rejected = false;
                try { controller.RequestReplay("original-wave", 5, "stale-reference"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Old binding replay survived rebind.");
                Check(Context()["lastExecutionFeedback"].Type == JTokenType.Null, "Rebind invented nested feedback facts.");
                report.success = true; Finish();
            }
        }
        catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); Finish(); }
    }

    private static void CheckResponseBeforeOwnershipLateUpdate(bool continuation)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ArdyLiveMotionController);
        var player = avatar.GetComponent<ArdyMotionPlayer>();
        controller.Cancel("regression-prepare-transport-boundary");
        string id = continuation ? "old-continuation-before-LateUpdate" : "old-first-window-before-LateUpdate";
        type.GetMethod("BeginAction", flags).Invoke(controller, new object[] {
            "generate", "Synthetic transport boundary fixture, not model output", 7, id, null, null, 0, null });
        // Construct the state at a completed transport callback without opening a socket.
        // No remote job exists: a null remote turn prevents Cancel from sending cleanup HTTP.
        type.GetField("liveRequest", flags).SetValue(controller, true);
        type.GetField("turnId", flags).SetValue(controller, null);
        type.GetField("activeMotionName", flags).SetValue(controller, "generate");
        type.GetField("activeMotionDescription", flags).SetValue(controller, "Synthetic transport boundary fixture");
        long epoch = controller.Revision;
        var apply = type.GetMethod("ApplyResponse", flags);
        var owns = type.GetMethod("OwnsActionPlayback", flags);
        Check((bool)owns.Invoke(controller, null), "Synthetic pending action did not own the pre-response Player identity.");

        ArdyGenerateRequest Request(int index) => new ArdyGenerateRequest {
            characterId = (string)type.GetField("characterId", flags).GetValue(controller),
            turnId = id, requestId = id + "-chunk-" + index, description = "Synthetic transport fixture",
            revision = epoch, chunkIndex = index, seed = 0 };
        ArdyGenerateResponse Response(ArdyGenerateRequest request)
        {
            var clip = ArdyMotionClip.Parse(JsonUtility.ToJson(firstPlayerClip));
            var frames = new ArdyMotionFrame[ArdyLiveProtocol.FramesPerChunk];
            Array.Copy(clip.frames, request.chunkIndex * frames.Length, frames, 0, frames.Length);
            clip.frames = frames;
            return new ArdyGenerateResponse {
                characterId = request.characterId, turnId = request.turnId, requestId = request.requestId,
                revision = epoch, chunkIndex = request.chunkIndex, startFrame = request.chunkIndex * frames.Length,
                newFrames = frames.Length, historyFrames = request.chunkIndex == 0 ? 0 : ArdyLiveProtocol.HistoryFrames,
                fps = 20, final = false, clip = clip, timings = new ArdyMotionTimings(),
                // These are validator fixtures only, never reported as real Qwen provenance.
                provenance = new ArdyMotionProvenance { mode = "dynamic-ardy-native",
                    featureSource = request.chunkIndex == 0 ? "live-qwen" : "live-condition-reused",
                    featureContract = ArdyLiveProtocol.FeatureContract, modelSha256 = ArdyLiveProtocol.ModelSha256,
                    historySource = "synthetic-regression-transport-fixture" } };
        }
        if (continuation)
        {
            var firstRequest = Request(0); var first = Response(firstRequest);
            ArdyLiveProtocol.ValidateResponse(first, firstRequest);
            Check((bool)apply.Invoke(controller, new object[] { first, firstRequest, epoch }),
                "Owned first window was not accepted before testing the stale continuation.");
        }
        var request = Request(continuation ? 1 : 0);
        var response = Response(request);
        ArdyLiveProtocol.ValidateResponse(response, request); // The rejection must be ownership, not malformed payload.
        int beforeChunks = controller.ReceivedChunks;
        int frameBefore = Time.frameCount;
        player.Play(Resources.Load<TextAsset>("ARDY/nod"), ArdyMotionMask.Head);
        transportSuccessor = PlayerClip(); transportSuccessorSerial = player.MotionPlaybackSerial;
        Check(!(bool)owns.Invoke(controller, null), "External Play did not revoke the old callback's ownership.");
        Check(controller.CurrentActionId == id, "The test accidentally allowed LateUpdate to retire the old action first.");
        bool accepted = (bool)apply.Invoke(controller, new object[] { response, request, epoch });
        Check(Time.frameCount == frameBefore, "Transport race fixture crossed a real frame before response application.");
        Check(!accepted, "A valid old response was applied before LateUpdate could detect external takeover.");
        Check(player.MotionPlaybackSerial == transportSuccessorSerial && ReferenceEquals(PlayerClip(), transportSuccessor)
            && player.IsPlaying && !player.MotionPlaybackWasInterrupted,
            "Rejecting an old transport callback changed or stopped its external successor.");
        Check(controller.CurrentActionId == null && controller.Revision > epoch
            && !(bool)type.GetField("liveRequest", flags).GetValue(controller), "Old callback retained session or remote request authority.");
        Check(controller.ReceivedChunks == beforeChunks && controller.LastReplayableActionId == "repeated-wave",
            "Rejected callback changed the received count or archived a partial transport action.");
        var context = Context();
        Check((string)context["lastExecutionFeedback"]["actionId"] == id
            && (string)context["lastExecutionFeedback"]["status"] == "cancelled", "Callback retirement lost its exact old action identity.");
        report.transportBoundaryCases.Add(id + ": protocol-valid synthetic callback; same-frame external Play before ApplyResponse; rejected without successor mutation; no HTTP/model request");
    }
    private static ArdyMotionClip PlayerClip() => (ArdyMotionClip)typeof(ArdyMotionPlayer)
        .GetField("clip", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(avatar.GetComponent<ArdyMotionPlayer>());
    private static void Finish()
    {
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/intent-replay-regression.json"));
        File.WriteAllText(path, JsonUtility.ToJson(report, true));
        SessionState.SetBool(Key + "Success", report.success);
        SessionState.SetBool(Key + "Finished", true);
        EditorApplication.isPlaying = false;
    }
    private static void Exit()
    {
        SessionState.SetBool(Key + "Active", false);
        EditorApplication.Exit(SessionState.GetBool(Key + "Success", false) ? 0 : 1);
    }
}
