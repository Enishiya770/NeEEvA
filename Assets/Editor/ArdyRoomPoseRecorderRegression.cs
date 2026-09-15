using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;

/// <summary>Recorder regression in the existing disposable project. No HTTP, generated test motions, or manual test scene.</summary>
[InitializeOnLoad]
public static class ArdyRoomPoseRecorderRegression
{
    private const string Key = "NeEEvA.ARDY.PoseRecorder.", AvatarName = "ARDY pose recorder fixture";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion, inputClip, exportedCapture;
        public string scope = "Actual imported VRM ControlRig and Animator in PlayMode; existing real ARDY response replayed through production player, confirmed playback end at frame 23. PostLateUpdate observers bracket the recorder. Separate storage-only fixtures exercise count/byte/age bounds and payload deduplication. No network, service or user-scene changes.";
        public int checks, observedRenderedFrames, recordedSamples, exactSourceSamples, handoffSamples, releasedClipSamples;
        public bool sawRejectedClockAheadOfAppliedPose, exportRoundTripped, controlRigEnabled;
        public float maximumRenderedPositionError, maximumRenderedRotationError, sourceSpanSeconds;
    }
    private sealed class BeforeRecorder { }
    private sealed class AfterRecorder { }
    private static Report report;
    private static Vrm10Instance avatar;
    private static ArdyRoomLocomotionController controller;
    private static ArdyRoomPoseRecorder recorder;
    private static ArdyLocomotionClip clip;
    private static Transform[] bones;
    private static Vector3[] beforePositions, beforeLocalPositions, beforeScales;
    private static Quaternion[] beforeRotations, beforeLocalRotations;
    private static Vector3 beforeRoot;
    private static Quaternion beforeRootRotation;
    private static long beforePlaybackRevision;
    private static bool beforeOwnership, rejectNextRoot, observing;
    private static int lastUnityFrame;
    private static Exception observationError;
    private static IEnumerator routine;

    static ArdyRoomPoseRecorderRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { try { Initialize(); } catch (Exception error) { Finish(error); } }
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
            throw new InvalidOperationException("Run only in the existing isolated unity-naturalness-validation project, -batchmode without -quit.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play mode is already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty validation scene.");
            string input = Path.GetFullPath(Argument("-ardyLocomotionClip", ""));
            var source = ArdyLocomotionClip.Parse(File.ReadAllText(input));
            if (source.frames.Length < 40) throw new InvalidOperationException("Supply an existing real ARDY clip with at least 40 frames.");
            string asset = Argument("-ardyRoomAvatar", "Assets/Model/NeEEvA.vrm");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (prefab == null) throw new InvalidOperationException("Missing imported VRM asset.");
            ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), asset).Validate();
            SessionState.SetString(Key + "Avatar", asset); SessionState.SetString(Key + "Clip", input);
            SessionState.SetString(Key + "Output", Path.GetFullPath(Argument("-ardyRoomOutput", "Logs/ardy-room-pose-recorder")));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab); instance.name = AvatarName;
            instance.transform.SetPositionAndRotation(new Vector3(.4f, .2f, -.6f), Quaternion.Euler(0, 37, 0));
            instance.transform.localScale = Vector3.one * .9f;
            var vrm = instance.GetComponent<Vrm10Instance>(); vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true))
            { animator.enabled = false; animator.runtimeAnimatorController = null; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            foreach (var renderer in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 90);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void Initialize()
    {
        report = new Report { unityVersion = Application.unityVersion, inputClip = SessionState.GetString(Key + "Clip", "") };
        clip = ArdyLocomotionClip.Parse(File.ReadAllText(report.inputClip));
        string asset = SessionState.GetString(Key + "Avatar", "");
        var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(asset).GetComponent<Vrm10Instance>(), asset);
        avatar = GameObject.Find(AvatarName).GetComponent<Vrm10Instance>();
        var flag = typeof(Vrm10Instance).GetField("m_useControlRig", Private);
        Check(flag != null, "VRM ControlRig import option is unavailable."); flag.SetValue(avatar, true);
        Check(avatar.Runtime.ControlRig != null, "Actual ControlRig was not constructed.");
        avatar.enabled = true; report.controlRigEnabled = true;
        var animator = avatar.GetComponent<Animator>();
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
        Check(animator.runtimeAnimatorController != null, "Missing actual Animator controller.");
        animator.enabled = true; animator.SetInteger("state", 0);
        var host = new GameObject("ARDY recorder observation host");
        // Configure needs a navigation component but this recorder fixture never plans or asks it to move.
        var navigation = host.AddComponent<ArdyRoomNavigation>();
        controller = host.AddComponent<ArdyRoomLocomotionController>();
        controller.Configure(avatar, reference, navigation);
        recorder = host.GetComponent<ArdyRoomPoseRecorder>();
        Check(recorder != null && recorder.RecordingEnabled, "Controller must auto-attach an enabled bounded recorder.");
        RunStorageChecks();
        report.checks += ArdyRoomStreamDiagnosticsRegression.RunChecks();
        report.checks += ArdyRoomStreamDiagnosticsRegression.RunArrivalChecks(controller, clip);
        bones = recorder.ReadCapture().bones.Select(b => avatar.Humanoid.GetBoneTransform(b.human)).ToArray();
        Check(bones.Length >= 19 && bones.Length <= (int)HumanBodyBones.LastBone, "Unexpected Humanoid capture bone count.");
        beforePositions = new Vector3[bones.Length]; beforeLocalPositions = new Vector3[bones.Length]; beforeScales = new Vector3[bones.Length];
        beforeRotations = new Quaternion[bones.Length]; beforeLocalRotations = new Quaternion[bones.Length];
        QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
        rejectNextRoot = false; observing = true; observationError = null; lastUnityFrame = -1;
        InstallObservers(true); routine = Scenarios();
    }

    private static IEnumerator Scenarios()
    {
        float until = Time.realtimeSinceStartup + .2f;
        while (Time.realtimeSinceStartup < until) yield return null;
        controller.Player.Play(clip, (from, to) => rejectNextRoot ? "recorder fixture root boundary" : null, 23);
        // Identifier-only fixture: a new controller operation appears before the first 20 Hz
        // observation of this already adopted clip. No new motion or network request is fabricated.
        controller.Player.SetPoseRecordingIdentity(17, 31, "recorder-adopted-session", 80);
        typeof(ArdyRoomLocomotionController).GetProperty("OperationRevision").SetValue(controller, 18);
        while (controller.Player.TimeSeconds < .65f) yield return null;
        Check(recorder.SampleCount >= 8, "Recorder did not observe enough real player-loop samples.");
        float previousApplied = controller.Player.LastAppliedSourceTimeSeconds;
        rejectNextRoot = true;
        until = Time.realtimeSinceStartup + 1.5f;
        while (!controller.Player.IsReturningToAnimation && Time.realtimeSinceStartup < until) yield return null;
        Check(controller.Player.IsReturningToAnimation, "Root guard did not enter the real smooth handoff.");
        Check(Mathf.Abs(controller.Player.LastAppliedSourceTimeSeconds - previousApplied) < .0001f,
            "Rejected root attempt was mislabeled as successfully applied source time.");
        Check(controller.Player.TimeSeconds > controller.Player.LastAppliedSourceTimeSeconds,
            "Fixture did not distinguish the attempted body clock from the last applied source frame.");
        while (controller.Player.HasPoseOwnership && Time.realtimeSinceStartup < until) yield return null;
        Check(!controller.Player.HasPoseOwnership, "Real handoff did not release the player.");
        controller.Player.Stop();
        until = Time.realtimeSinceStartup + .3f;
        while (Time.realtimeSinceStartup < until) yield return null;
        var capture = recorder.ReadCapture(); report.recordedSamples = capture.samples.Length;
        report.sourceSpanSeconds = capture.observedSpanSeconds;
        Check(report.exactSourceSamples >= 8 && report.handoffSamples >= 3 && report.releasedClipSamples >= 3,
            "Missing exact source, animation handoff, or released/canceled-clip observations.");
        Check(report.sawRejectedClockAheadOfAppliedPose, "No captured evidence separated the rejected clock from the frozen source pose.");
        Check(capture.clips.Length > 0 && capture.clips[0].applicationBindings.Any(b => b.drivesControlRig),
            "Export omitted actual ControlRig application bases.");
        Check(capture.clips[0].identityKnown && capture.clips[0].operationRevision == 17 && capture.clips[0].wireRevision == 31 &&
            capture.clips[0].globalBaseFrame == 80 && capture.clips[0].sessionId == "recorder-adopted-session" &&
            capture.samples.Any(s => s.operationRevision == 18 && s.staleClipAfterRelease),
            "An old/canceled clip was relabeled using a newer controller operation before its first observation.");
        report.exportedCapture = recorder.ExportRecent();
        var exported = JsonUtility.FromJson<ArdyRoomPoseRecorder.Capture>(File.ReadAllText(report.exportedCapture));
        Check(exported.schema == 1 && exported.samples.Length == capture.samples.Length && exported.bones.Length == bones.Length,
            "Export schema/sample/bone identities failed JSON round trip.");
        Check(new FileInfo(report.exportedCapture).Length <= ArdyRoomPoseRecorder.MaximumExportBytes, "Export exceeded its byte bound.");
        report.exportRoundTripped = true;
        recorder.RecordingEnabled = false;
        int count = recorder.SampleCount;
        until = Time.realtimeSinceStartup + .15f;
        while (Time.realtimeSinceStartup < until) yield return null;
        Check(recorder.SampleCount == count, "Disabled recorder continued collecting observations.");
    }

    private static void RunStorageChecks()
    {
        var host = new GameObject("ARDY recorder storage-only fixture");
        var storage = host.AddComponent<ArdyRoomPoseRecorder>(); storage.RecordingEnabled = false;
        try
        {
            for (int i = 0; i < 1000; i++) Invoke(storage, "AddSample", new ArdyRoomPoseRecorder.PoseSample { sequence = i, realtime = i * .025 });
            var capture = storage.ReadCapture();
            Check(capture.samples.Length == ArdyRoomPoseRecorder.MaximumSamples && capture.samples[0].sequence == 800 &&
                capture.samples[capture.samples.Length - 1].sequence == 999, "Count-bound ring did not retain the latest ordered samples.");
            Invoke(storage, "AddSample", new ArdyRoomPoseRecorder.PoseSample { sequence = 1000, realtime = 60 });
            Check(storage.SampleCount == 1, "A large frame gap retained samples older than the 10-second window.");
            string payload = new string('中', 40000); int bytes = Encoding.UTF8.GetByteCount(payload);
            for (int i = 0; i < 100; i++) Invoke(storage, "AddAcceptedSegment", new ArdyRoomPoseRecorder.AcceptedSegment {
                requestId = "storage-only-" + i, responseJson = payload, rawUtf8Bytes = bytes });
            capture = storage.ReadCapture();
            Check(capture.acceptedSegments.Length <= ArdyRoomPoseRecorder.MaximumSegments && capture.rawPayloadUtf8Bytes <= ArdyRoomPoseRecorder.MaximumRawPayloadBytes,
                "Raw segment memory exceeded its count or UTF-8 byte bound.");
            Check(capture.rawPayloadUtf8Bytes == capture.acceptedSegments.Sum(s => Encoding.UTF8.GetByteCount(s.responseJson ?? "")) &&
                capture.acceptedSegments.Last().requestId == "storage-only-99", "Raw payload accounting or newest segment retention is wrong.");
            var context = new ArdyRoomPoseRecorder.ClipContext { globalBaseFrame = 100 };
            var beyond = new ArdyRoomPoseRecorder.PoseSample();
            Invoke(storage, "ReadConfirmedSource", beyond, clip, context, .4f, .35f, true);
            Check(!beyond.sourceWithinConfirmedPrefix && beyond.sampledSourceGlobalRotations == null, "Unconfirmed tail was labeled or serialized as played source.");
            var within = new ArdyRoomPoseRecorder.PoseSample();
            Invoke(storage, "ReadConfirmedSource", within, clip, context, .325f, .35f, true);
            Check(within.sourceWithinConfirmedPrefix && within.globalFrameA == 106 && within.globalFrameB == 107 && Mathf.Abs(within.frameBlend - .5f) < .0001f,
                "Source frame alignment lost the historical base frame or interpolation fraction.");
            for (int j = 0; j < 27; j++) Check(Quaternion.Angle(within.sampledSourceDeltas[j], clip.rotationClip.SampleDelta(j, .325f)) < .08f,
                "Recorded source rotation does not reproduce the actual sampling interpolation.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }

        var dedupHost = new GameObject("ARDY recorder metadata-only dedup fixture");
        var dedup = dedupHost.AddComponent<ArdyRoomPoseRecorder>(); dedup.RecordingEnabled = false;
        try
        {
            var owner = dedupHost.AddComponent<ArdyRoomLocomotionController>();
            typeof(ArdyRoomPoseRecorder).GetField("controller", Private).SetValue(dedup, owner);
            var evidence = (List<ArdyRoomLocomotionController.SegmentEvidence>)typeof(ArdyRoomLocomotionController).GetField("segmentEvidence", Private).GetValue(owner);
            for (int i = 0; i < 64; i++) evidence.Add(new ArdyRoomLocomotionController.SegmentEvidence {
                requestId = "metadata-only-" + i, wireRevision = 1, outcome = "stream-accepted", startFrame = i * 8,
                requestJson = "{\"sessionId\":\"metadata-only\",\"targets\":[]}", responseJson = "{\"commitFrames\":8}" });
            Invoke(dedup, "CaptureAcceptedSegments"); var once = dedup.ReadCapture();
            Invoke(dedup, "CaptureAcceptedSegments"); var twice = dedup.ReadCapture();
            Check(once.acceptedSegments.Length == 32 && twice.evictedSegments == once.evictedSegments && twice.rawPayloadUtf8Bytes == once.rawPayloadUtf8Bytes,
                "Older controller evidence was repeatedly re-added after payload eviction.");
        }
        finally { UnityEngine.Object.DestroyImmediate(dedupHost); }
    }

    private static void Before()
    {
        if (!observing || avatar == null) return;
        beforeRoot = avatar.transform.position; beforeRootRotation = avatar.transform.rotation;
        beforePlaybackRevision = controller.Player.PlaybackRevision; beforeOwnership = controller.Player.HasPoseOwnership;
        for (int i = 0; i < bones.Length; i++)
        { beforePositions[i] = bones[i].position; beforeRotations[i] = bones[i].rotation; beforeLocalPositions[i] = bones[i].localPosition;
            beforeLocalRotations[i] = bones[i].localRotation; beforeScales[i] = bones[i].localScale; }
    }

    private static void After()
    {
        if (!observing || avatar == null || observationError != null) return;
        try
        {
            Check(avatar.transform.position == beforeRoot && avatar.transform.rotation == beforeRootRotation &&
                controller.Player.PlaybackRevision == beforePlaybackRevision && controller.Player.HasPoseOwnership == beforeOwnership,
                "Recorder changed the root or playback ownership.");
            for (int i = 0; i < bones.Length; i++)
                Check(bones[i].position == beforePositions[i] && bones[i].rotation == beforeRotations[i] && bones[i].localPosition == beforeLocalPositions[i] &&
                    bones[i].localRotation == beforeLocalRotations[i] && bones[i].localScale == beforeScales[i], "Recorder changed an observed bone transform.");
            report.observedRenderedFrames++;
            var capture = recorder.ReadCapture(); if (capture.samples.Length == 0) return;
            var sample = capture.samples[capture.samples.Length - 1]; if (sample.unityFrame != Time.frameCount) return;
            Check(sample.renderedHumanoid.Length == bones.Length && sample.unityFrame == Time.frameCount, "Recorder frame or bone alignment is wrong.");
            for (int i = 0; i < bones.Length; i++)
            {
                float positionError = Vector3.Distance(sample.renderedHumanoid[i].worldPosition, bones[i].position);
                float angleError = Quaternion.Angle(sample.renderedHumanoid[i].worldRotation, bones[i].rotation);
                report.maximumRenderedPositionError = Mathf.Max(report.maximumRenderedPositionError, positionError);
                report.maximumRenderedRotationError = Mathf.Max(report.maximumRenderedRotationError, angleError);
                Check(positionError < .00001f && angleError < .08f, "Recorder did not capture the final post-VRM Humanoid pose.");
            }
            if (sample.sourceWithinConfirmedPrefix)
            {
                Check(sample.localFrameB <= 23 && sample.appliedSourceSeconds <= 23f / 20f + .00001f,
                    "Recorder labeled the unconfirmed candidate tail as played.");
                for (int j = 0; j < 27; j++) Check(Quaternion.Angle(sample.sampledSourceDeltas[j], clip.rotationClip.SampleDelta(j, sample.appliedSourceSeconds)) < .08f,
                    "Recorded source time/rotation did not match the actual applied clip sample.");
            }
            if (sample.sourceAppliedThisFrame)
            { report.exactSourceSamples++; Check(sample.appliedSourceUnityFrame == sample.unityFrame && !sample.returningToAnimation,
                "An old or handoff source pose was mislabeled as applied this Unity frame."); }
            if (sample.returningToAnimation)
            {
                report.handoffSamples++; Check(!sample.sourceAppliedThisFrame, "Animator handoff was mislabeled as direct source playback.");
                if (sample.bodyClockSeconds > sample.appliedSourceSeconds + .0001f) report.sawRejectedClockAheadOfAppliedPose = true;
            }
            if (sample.staleClipAfterRelease)
            { report.releasedClipSamples++; Check(!sample.sourceAppliedThisFrame && sample.sampledSourceGlobalRotations == null && !sample.poseOwned,
                "A retained canceled clip was serialized as currently applied motion."); }
        }
        catch (Exception error) { observationError = error; }
    }

    private static void InstallObservers(bool install)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop(); Rewrite(ref loop); PlayerLoop.SetPlayerLoop(loop);
        void Rewrite(ref PlayerLoopSystem item)
        {
            if (item.subSystemList == null) return;
            var result = new List<PlayerLoopSystem>();
            foreach (var original in item.subSystemList)
            {
                if (original.type == typeof(BeforeRecorder) || original.type == typeof(AfterRecorder)) continue;
                if (install && original.type == typeof(ArdyRoomPoseRecorder).GetNestedType("PoseRecorderLoop", BindingFlags.NonPublic))
                    result.Add(new PlayerLoopSystem { type = typeof(BeforeRecorder), updateDelegate = Before });
                var child = original; Rewrite(ref child); result.Add(child);
            }
            if (install && item.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                result.Add(new PlayerLoopSystem { type = typeof(AfterRecorder), updateDelegate = After });
            item.subSystemList = result.ToArray();
        }
    }
    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
        { Finish(new TimeoutException("Pose recorder regression exceeded its 90 second deadline.")); return; }
        if (!EditorApplication.isPlaying || routine == null || Time.frameCount == lastUnityFrame) return;
        lastUnityFrame = Time.frameCount;
        try { if (observationError != null) throw observationError; if (!routine.MoveNext()) Finish(null); }
        catch (Exception error) { Finish(error); }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        SessionState.SetBool(Key + "Finished", true); observing = false; InstallObservers(false);
        if (report == null) report = new Report(); report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        try { controller?.Player?.Stop(); } catch (Exception cleanup) { report.status = "failed"; report.error += "\nCleanup: " + cleanup; }
        string output = SessionState.GetString(Key + "Output", "Logs/ardy-room-pose-recorder");
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "pose-recorder.json"), JsonUtility.ToJson(report, true));
        SessionState.SetInt(Key + "Exit", report.status == "passed" ? 0 : 1);
        if (error != null) Debug.LogException(error);
        EditorApplication.isPlaying = false;
    }
    private static void FinalizeBatch()
    { int code = SessionState.GetInt(Key + "Exit", 1); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(code); }
    private static void Invoke(object instance, string method, params object[] args)
    { typeof(ArdyRoomPoseRecorder).GetMethod(method, Private).Invoke(instance, args); }
    private static void Check(bool condition, string reason) { report.checks++; if (!condition) throw new InvalidOperationException(reason); }
    private static string Argument(string name, string fallback)
    { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
}
