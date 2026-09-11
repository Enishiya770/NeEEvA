using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NeEEvA.Motion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Eight preregistered real HTTP/Animator/VRM observations. A missed goal is a result, never a reason to retry.</summary>
[InitializeOnLoad]
public static class ArdyIntentGeneralizationRegression
{
    private const string Key = "NeEEvA.ARDY.IntentGeneralization.";
    private const string AvatarPath = "Assets/Model/NEVA.vrm";
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private const string NearHeadText = "Bring both hands beside the head, keeping the body facing forward.";
    private const string ForwardText = "Extend both arms forward at shoulder height, then hold them there.";
    // A public fixed matrix, not descriptions chosen after seeing generated motion.
    private const string MatrixSignature = "intent-generalization-native-v1\n"
        + "near-head|" + NearHeadText + "\nforward|" + ForwardText
        + "\nhistories=idle-Animator,left-wave-at-0.7s\nseeds=0,1\nconditioning=16\nstableSeconds=0.2\navatar=NEVA\n";

    [Serializable] private sealed class PlanRow
    {
        public string id, originalIntent, description, history;
        public ArdyMotionGoal goal;
        public int seed;
    }
    [Serializable] private sealed class MatrixPlan
    {
        public string matrixId = "intent-generalization-native-v1", matrixSignature, matrixSignatureSha256;
        public string frozenBeforeAnyRequestUtc, sourceSha256, avatarSha256, animatorSha256, leftWaveSha256;
        public string serviceUrl;
        public int conditioningFrames = 16, deadlineSeconds = 180;
        public string sampling = "Real Animator, Player LateUpdate10000, VRM LateUpdate, production Observer12100, independent recorder12500. Target60fps; no manual Tick or Animator.Update.";
        public string scoring = "All requested necessary wrist regions simultaneously observed for at least0.2 seconds. Full semantics and naturalness are not established.";
        public List<PlanRow> rows = new List<PlanRow>();
        public List<SourceHash> sources = new List<SourceHash>();
    }
    [Serializable] private sealed class SourceHash { public string path, sha256; }
    [Serializable] private sealed class TimedFeedback { public double realtime; public ArdyMotionExecutionFeedback feedback; }
    [Serializable] private sealed class Window
    {
        public int chunkIndex, sourceFrameCount;
        public long httpStatus;
        public double requestAtSeconds, responseAtSeconds, httpRoundTripSeconds;
        public string requestId, frameSha256, featureSource, modelSha256, adapterSha256;
        public ArdyMotionTimings timings;
    }
    [Serializable] private sealed class Row
    {
        public PlanRow planned;
        public string actionId, precedingActionId, status = "not-started", controllerStatus, error;
        public string necessaryGoalStatus = "unknown", archivedTracePath, archivedTraceSha256;
        public string initialHistorySha256, sourceFramesSha256, actualSamplesPath, actualSamplesSha256;
        public string contextAtDispatch, contextAtCompletion;
        public int responseGeneration, receivedChunks, initialHistoryFrames, sourceFrameCount, recordedActualSamples;
        public long revision;
        public double preparationStartedAt, requestStartedAt, completedAt, firstWindowSeconds = -1, allWindowsSeconds = -1;
        public double actualWaveTimeAtHandoff = -1, actualWaveElapsedAtHandoff = -1;
        public bool initialHistoryWasMeasured, naturallyCompleted, infrastructurePassed;
        public ArdyMotionPoseSample actualPoseAtHandoff;
        public ArdyMotionExecutionFeedback finalFeedback;
        public ArdyMotionObservation finalObservation;
        public List<TimedFeedback> feedback = new List<TimedFeedback>();
        public List<Window> windows = new List<Window>();
        public List<string> infrastructureErrors = new List<string>();
    }
    [Serializable] private sealed class SampleRecord
    {
        public string actionId;
        public string measurementSource = "actual-humanoid-after-player-blending-and-VRM";
        public List<ArdyMotionObservation> observations = new List<ArdyMotionObservation>();
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", unityVersion, startedUtc, finishedUtc, error, planPath, planSha256;
        public string scope = "Fixed8 real HTTP generations, no extra Qwen chat, no automatic repair, no seed/description selection. Missed goals remain results.";
        public string branch = "Actual NEVA, original Animator, enabled real ControlRig and VRM LateUpdate";
        public int plannedRows = 8, completedRows, reachedRows, notReachedRows, unknownRows, infrastructureFailedRows;
        public bool manualTickUsed, servicesStarted, privateSceneLoaded;
        public List<Row> rows = new List<Row>();
    }

    private static MatrixPlan plan;
    private static Report report;
    private static Vrm10Instance avatar;
    private static Animator animator;
    private static ArdyLiveMotionController controller;
    private static ArdyMotionPlayer player;
    private static ArdyMotionObserver observer;
    private static Row current;
    private static SampleRecord samples;
    private static int rowIndex, stage, lastObservedFrame = -1;
    private static double stageAt;
    private static string output, directory, currentTrace;

    static ArdyIntentGeneralizationRegression()
    {
        EditorApplication.update += CheckDeadline;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Initialize();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Finish();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Finish();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the isolated unity-naturalness-validation project, in batch mode.");
        try
        {
            output = Path.GetFullPath(Argument("-ardyIntentReport", Path.Combine(Application.dataPath, "../Logs/ardy-intent-generalization-v1/report.json")));
            directory = Path.GetDirectoryName(output);
            string url = Argument("-ardyServiceUrl", "http://127.0.0.1:8093");
            if (File.Exists(output) || File.Exists(Path.Combine(directory, "plan.json")))
                throw new InvalidOperationException("Use a new output directory; a preregistered run is never overwritten.");
            Directory.CreateDirectory(directory);
            plan = BuildPlan(url);
            WriteNew(Path.Combine(directory, "plan.json"), JsonUtility.ToJson(plan, true));
            SessionState.SetString(Key + "Output", output);
            SessionState.SetString(Key + "ServiceUrl", url);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var animation = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
            if (prefab == null || animation == null) throw new InvalidOperationException("Actual NEVA/Animator asset missing.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            root.name = "ARDY-Intent-Matrix-NEVA";
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 37, 0));
            var vrm = root.GetComponent<Vrm10Instance>();
            vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
            var anim = root.GetComponent<Animator>();
            anim.runtimeAnimatorController = animation;
            anim.enabled = true; anim.applyRootMotion = false; anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            SessionState.SetBool(Key + "Active", true);
            SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 180);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static MatrixPlan BuildPlan(string url)
    {
        var value = new MatrixPlan { matrixSignature = MatrixSignature, matrixSignatureSha256 = HashText(MatrixSignature),
            frozenBeforeAnyRequestUtc = DateTime.UtcNow.ToString("o"), serviceUrl = url,
            sourceSha256 = HashFile("Assets/Editor/ArdyIntentGeneralizationRegression.cs"),
            avatarSha256 = HashFile(AvatarPath), animatorSha256 = HashFile(AnimatorPath),
            leftWaveSha256 = HashFile("Assets/AIChatTookit/Resources/ARDY/left-wave.json") };
        foreach (string goal in new[] { "near-head", "forward" })
            foreach (string history in new[] { "idle-Animator", "left-wave-at-0.7s" })
                foreach (int seed in new[] { 0, 1 })
                    value.rows.Add(new PlanRow { id = goal + "-" + (history == "idle-Animator" ? "idle" : "wave") + "-seed" + seed,
                        originalIntent = goal == "near-head" ? "Both hands beside the head; no finger-shape requirement." : "Both arms extended in front at shoulder height.",
                        description = goal == "near-head" ? NearHeadText : ForwardText, history = history, seed = seed,
                        goal = new ArdyMotionGoal { leftGoal = goal, rightGoal = goal, requiredStableSeconds = .2f } });
        foreach (string path in new[] { "Assets/AIChatTookit/Scripts/Motion/ArdyLiveMotionController.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyLiveMotionController.Intent.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyMotionObserver.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyMotionObservation.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyMotionGoal.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyLiveProtocol.cs" })
            value.sources.Add(new SourceHash { path = path, sha256 = HashFile(path) });
        return value;
    }

    private static void Initialize()
    {
        try
        {
            output = SessionState.GetString(Key + "Output", ""); directory = Path.GetDirectoryName(output);
            string planPath = Path.Combine(directory, "plan.json");
            plan = JsonUtility.FromJson<MatrixPlan>(File.ReadAllText(planPath));
            if (plan.matrixSignature != MatrixSignature || plan.rows.Count != 8) throw new InvalidOperationException("Frozen matrix changed across the PlayMode reload.");
            report = new Report { unityVersion = Application.unityVersion, startedUtc = DateTime.UtcNow.ToString("o"),
                planPath = planPath, planSha256 = HashFile(planPath) };
            foreach (var planned in plan.rows) report.rows.Add(new Row { planned = planned });
            avatar = GameObject.Find("ARDY-Intent-Matrix-NEVA").GetComponent<Vrm10Instance>();
            animator = avatar.GetComponent<Animator>();
            typeof(Vrm10Instance).GetField("m_useControlRig", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(avatar, true);
            if (avatar.Runtime.ControlRig == null) throw new InvalidOperationException("Actual VRM ControlRig was not constructed.");
            avatar.enabled = true;
            foreach (var mesh in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)) mesh.updateWhenOffscreen = true;
            controller = avatar.gameObject.AddComponent<ArdyLiveMotionController>();
            controller.serviceUrl = plan.serviceUrl; controller.initialConditioningFrames = 16; controller.recordMotionDiagnostics = true;
            controller.allowGeneratedMotion = true;
            controller.MotionFeedback += OnFeedback;
            avatar.gameObject.AddComponent<ArdyIntentGeneralizationSampler>();
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
            if (Time.timeScale != 1 || Time.captureDeltaTime != 0) throw new InvalidOperationException("Actual realtime sampling requires unmodified game-time progression.");
            rowIndex = 0;
            BeginRow();
        }
        catch (Exception error) { Fail(error); }
    }

    private static void BeginRow()
    {
        current = report.rows[rowIndex];
        current.actionId = "intent-matrix-v1:" + (rowIndex + 1) + ":generated";
        current.precedingActionId = "intent-matrix-v1:" + (rowIndex + 1) + ":history-wave";
        current.responseGeneration = 100 + rowIndex * 2;
        current.status = "preparing-history";
        current.preparationStartedAt = Time.realtimeSinceStartupAsDouble;
        samples = new SampleRecord { actionId = current.actionId };
        currentTrace = null; lastObservedFrame = -1;
        controller.Bind(avatar);
        player = avatar.GetComponent<ArdyMotionPlayer>(); observer = avatar.GetComponent<ArdyMotionObserver>();
        animator.SetInteger("state", 0); animator.Play("Base Layer.idle", 0, 0);
        stage = 0; stageAt = Time.timeAsDouble;
        SaveProgress();
    }

    internal static void AfterVrmFrame()
    {
        if (report == null || current == null || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (stage == 0)
            {
                // A full real16-frame Animator history, captured by the Controller itself.
                if (Time.timeAsDouble - stageAt < 1.2) return;
                if (current.planned.history == "left-wave-at-0.7s")
                {
                    controller.RequestMotion("left-wave", "", current.responseGeneration, current.precedingActionId);
                    stage = 1; stageAt = Time.timeAsDouble;
                }
                else StartGenerated();
                return;
            }
            if (stage == 1)
            {
                if (Time.timeAsDouble - stageAt < .7) return;
                current.actualWaveElapsedAtHandoff = Time.timeAsDouble - stageAt;
                current.actualWaveTimeAtHandoff = Convert.ToDouble(typeof(ArdyMotionPlayer).GetField("time", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(player));
                if (!player.IsPlaying) throw new InvalidOperationException("The actual left-wave resource ended before the preregistered0.7s handoff.");
                if (player.TryCaptureActualRenderedMotion(out var handoff)) current.actualPoseAtHandoff = handoff;
                StartGenerated();
                return;
            }
            if (stage != 2) return;
            double elapsed = Time.realtimeSinceStartupAsDouble - current.requestStartedAt;
            current.receivedChunks = controller.ReceivedChunks;
            if (current.receivedChunks > 0 && current.firstWindowSeconds < 0) current.firstWindowSeconds = elapsed;
            if (current.receivedChunks == 3 && current.allWindowsSeconds < 0) current.allWindowsSeconds = elapsed;
            var observation = observer.Observation;
            if (observation?.actionId == current.actionId && observation.actual != null && observation.lastFrame != lastObservedFrame)
            {
                lastObservedFrame = observation.lastFrame;
                samples.observations.Add(observation.Copy());
            }
            if (current.finalFeedback != null && string.IsNullOrEmpty(controller.CurrentActionId) && !player.IsPlaying)
            { CompleteRow(); return; }
            if (elapsed > 22)
            {
                current.error = "Row exceeded22s; no retry was made.";
                controller.Cancel("intent-matrix-row-timeout");
                CompleteRow();
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private static void StartGenerated()
    {
        current.contextAtDispatch = controller.DescribeMotionContext();
        controller.seed = current.planned.seed;
        current.requestStartedAt = Time.realtimeSinceStartupAsDouble;
        current.status = "generating";
        // Only the existing motion feature HTTP path is used; no Chat/Qwen conversation request.
        controller.RequestMotion("generate", current.planned.description, current.responseGeneration + 1,
            current.actionId, current.planned.goal.Copy(), null, 0);
        current.revision = controller.Revision;
        currentTrace = controller.LastDiagnosticPath;
        stage = 2;
    }

    private static void OnFeedback(ArdyMotionExecutionFeedback value)
    {
        if (current == null || value.actionId != current.actionId) return;
        current.feedback.Add(new TimedFeedback { realtime = Time.realtimeSinceStartupAsDouble, feedback = value.Copy() });
        if (value.status == "completed" || value.status == "goal-unmet" || value.status == "cancelled" || value.status == "rejected")
            current.finalFeedback = value.Copy();
    }

    private static void CompleteRow()
    {
        current.completedAt = Time.realtimeSinceStartupAsDouble;
        current.controllerStatus = controller.Status;
        current.contextAtCompletion = controller.DescribeMotionContext();
        current.finalObservation = current.finalFeedback?.observation?.Copy();
        current.necessaryGoalStatus = current.finalObservation?.status ?? "unknown";
        current.status = !string.IsNullOrEmpty(current.error) ? "timeout-or-request-error" : current.finalFeedback?.status ?? "missing-terminal-feedback";
        current.naturallyCompleted = player.MotionPlaybackCompletedNaturally && !player.MotionPlaybackWasInterrupted;
        current.recordedActualSamples = samples.observations.Count;
        current.actualSamplesPath = Path.Combine(directory, current.planned.id + ".actual.json");
        WriteNew(current.actualSamplesPath, JsonUtility.ToJson(samples));
        current.actualSamplesSha256 = HashFile(current.actualSamplesPath);
        // Archive before Bind/next request; the production trace ring retains only8 files.
        try { ArchiveTrace(); }
        catch (Exception error) { current.infrastructureErrors.Add("Trace audit: " + error.Message); }
        if (current.finalFeedback == null) current.infrastructureErrors.Add("No terminal action-bound feedback.");
        else if (current.finalFeedback.actionId != current.actionId || current.finalFeedback.repairAttempt != 0 ||
            !string.IsNullOrEmpty(current.finalFeedback.parentActionId)) current.infrastructureErrors.Add("Feedback action/repair identity changed.");
        if (current.finalObservation?.actionId != current.actionId) current.infrastructureErrors.Add("Final actual observation does not bind this action.");
        if (current.recordedActualSamples == 0) current.infrastructureErrors.Add("No actual post-VRM samples were recorded.");
        current.infrastructurePassed = current.infrastructureErrors.Count == 0 && current.error == null;
        report.completedRows++;
        if (current.necessaryGoalStatus == "reached") report.reachedRows++;
        else if (current.necessaryGoalStatus == "not-reached") report.notReachedRows++;
        else report.unknownRows++;
        if (!current.infrastructurePassed) report.infrastructureFailedRows++;
        Debug.Log("[ArdyIntentMatrix] " + current.planned.id + " status=" + current.status + " necessaryGoal=" + current.necessaryGoalStatus);
        SaveProgress();
        rowIndex++;
        if (rowIndex < report.rows.Count) { BeginRow(); return; }
        report.status = report.infrastructureFailedRows == 0 ? "completed" : "completed-with-infrastructure-errors";
        SaveAndExitPlay();
    }

    private static void ArchiveTrace()
    {
        if (string.IsNullOrEmpty(currentTrace) || !File.Exists(currentTrace)) throw new InvalidOperationException("Missing actual HTTP trace.");
        current.archivedTracePath = Path.Combine(directory, current.planned.id + ".http.json");
        File.Copy(currentTrace, current.archivedTracePath, false);
        current.archivedTraceSha256 = HashFile(current.archivedTracePath);
        var trace = JObject.Parse(File.ReadAllText(current.archivedTracePath));
        if ((string)trace["actionId"] != current.actionId || (int)trace["seed"] != current.planned.seed || (string)trace["description"] != current.planned.description)
            throw new InvalidOperationException("HTTP trace action/seed/description differs from frozen row.");
        var allFrames = new JArray();
        JObject pending = null;
        double sentAt = 0;
        foreach (JObject entry in (JArray)trace["entries"])
        {
            string kind = (string)entry["kind"];
            if (kind == "request")
            {
                pending = JObject.Parse((string)entry["json"]); sentAt = (double)entry["elapsedSeconds"];
                if ((int)pending["chunkIndex"] == 0)
                {
                    var history = pending["initialHistory"];
                    var frames = (JArray)history?["frames"];
                    current.initialHistoryFrames = frames?.Count ?? 0;
                    current.initialHistorySha256 = HashText(history?.ToString(Formatting.None) ?? "null");
                    current.initialHistoryWasMeasured = (string)history?["kind"] == "unity-upper-body-projection-v1" && current.initialHistoryFrames == 16;
                }
            }
            else if (kind == "response")
            {
                if (pending == null) throw new InvalidOperationException("Unpaired response.");
                long status = (long)entry["httpStatus"];
                var row = new Window { chunkIndex = (int)pending["chunkIndex"], requestId = (string)pending["requestId"],
                    httpStatus = status, requestAtSeconds = sentAt, responseAtSeconds = (double)entry["elapsedSeconds"] };
                row.httpRoundTripSeconds = row.responseAtSeconds - row.requestAtSeconds;
                if (status == 200)
                {
                    var response = JObject.Parse((string)entry["json"]);
                    var request = JsonUtility.FromJson<ArdyGenerateRequest>(pending.ToString());
                    if (row.chunkIndex > 0) request.initialHistory = null;
                    ArdyLiveProtocol.ValidateResponse(JsonUtility.FromJson<ArdyGenerateResponse>(response.ToString()), request);
                    var frames = (JArray)response["clip"]?["frames"];
                    row.sourceFrameCount = frames?.Count ?? 0;
                    row.frameSha256 = HashText(frames?.ToString(Formatting.None) ?? "null");
                    foreach (var frame in frames) allFrames.Add(frame.DeepClone());
                    row.featureSource = (string)response["provenance"]?["featureSource"];
                    row.modelSha256 = (string)response["provenance"]?["modelSha256"];
                    row.adapterSha256 = (string)response["clip"]?["source"]?["adapterSha256"];
                    row.timings = JsonUtility.FromJson<ArdyMotionTimings>(response["timings"].ToString());
                }
                else current.infrastructureErrors.Add("HTTP " + status + " for chunk" + row.chunkIndex);
                current.windows.Add(row); pending = null;
            }
        }
        current.sourceFrameCount = allFrames.Count;
        current.sourceFramesSha256 = HashText(allFrames.ToString(Formatting.None));
        if (!current.initialHistoryWasMeasured) current.infrastructureErrors.Add("Expected16 actual supplied initial-history frames.");
        if (current.windows.Count != 3 || current.sourceFrameCount != 120) current.infrastructureErrors.Add("Did not receive3 complete40-frame windows.");
    }

    private static void CheckDeadline()
    {
        if (SessionState.GetBool(Key + "Active", false) && !SessionState.GetBool(Key + "Finished", false) &&
            EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue))
            Fail(new TimeoutException("Fixed matrix exceeded180s. Unexecuted rows remain in the report; no retries."));
    }
    private static void Fail(Exception error)
    {
        Debug.LogException(error);
        if (report == null) report = new Report();
        report.status = "harness-failed"; report.error = error.ToString();
        if (current != null && current.status != "completed" && current.status != "goal-unmet")
        {
            current.error = error.Message;
            controller?.Cancel("intent-matrix-harness-failed");
            try { if (!string.IsNullOrEmpty(currentTrace) && current.archivedTracePath == null) ArchiveTrace(); } catch (Exception archiveError) { current.infrastructureErrors.Add(archiveError.Message); }
            if (samples != null && current.actualSamplesPath == null)
            {
                try
                {
                    current.recordedActualSamples = samples.observations.Count;
                    current.actualSamplesPath = Path.Combine(directory, current.planned.id + ".actual.json");
                    WriteNew(current.actualSamplesPath, JsonUtility.ToJson(samples));
                    current.actualSamplesSha256 = HashFile(current.actualSamplesPath);
                }
                catch (Exception sampleError) { current.infrastructureErrors.Add(sampleError.Message); }
            }
            current.finalObservation = current.finalFeedback?.observation?.Copy();
            current.necessaryGoalStatus = current.finalObservation?.status ?? "unknown";
        }
        SaveAndExitPlay();
    }
    private static void SaveProgress()
    {
        if (string.IsNullOrEmpty(output)) return;
        var json = JObject.Parse(JsonUtility.ToJson(report, true));
        json["naturalness"] = JValue.CreateNull(); json["fullSemanticSuccess"] = JValue.CreateNull();
        var rows = (JArray)json["rows"];
        for (int i = 0; i < report.rows.Count; i++)
        {
            var row = (JObject)rows[i];
            row["naturalness"] = JValue.CreateNull(); row["fullSemanticSuccess"] = JValue.CreateNull();
            row["finalFeedback"] = FeedbackJson(report.rows[i].finalFeedback);
            row["finalObservation"] = ObservationJson(report.rows[i].finalObservation);
            var feedback = new JArray();
            foreach (var item in report.rows[i].feedback)
                feedback.Add(new JObject { ["realtime"] = item.realtime, ["feedback"] = FeedbackJson(item.feedback) });
            row["feedback"] = feedback;
            if (report.rows[i].actualPoseAtHandoff == null) row["actualPoseAtHandoff"] = JValue.CreateNull();
        }
        File.WriteAllText(output, json.ToString(Formatting.Indented), new UTF8Encoding(false));
    }
    private static JToken FeedbackJson(ArdyMotionExecutionFeedback value)
    {
        if (value == null) return JValue.CreateNull();
        var json = JObject.Parse(JsonUtility.ToJson(value));
        if (value.goal == null) json["goal"] = JValue.CreateNull();
        json["observation"] = ObservationJson(value.observation);
        return json;
    }
    private static JToken ObservationJson(ArdyMotionObservation value)
    {
        if (value == null) return JValue.CreateNull();
        var json = JObject.Parse(JsonUtility.ToJson(value));
        if (value.goal == null) json["goal"] = JValue.CreateNull();
        if (value.actual == null) json["actual"] = JValue.CreateNull();
        return json;
    }
    private static void SaveAndExitPlay()
    {
        if (report != null) report.finishedUtc = DateTime.UtcNow.ToString("o");
        SaveProgress();
        SessionState.SetBool(Key + "Passed", report?.status == "completed");
        SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Finish();
    }
    private static void Finish()
    {
        SessionState.SetBool(Key + "Active", false);
        EditorApplication.Exit(SessionState.GetBool(Key + "Passed", false) ? 0 : 1);
    }
    private static string Argument(string name, string fallback)
    {
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, name);
        if (at < 0) return fallback;
        if (at + 1 >= args.Length) throw new ArgumentException("Missing value for " + name);
        return args[at + 1];
    }
    private static void WriteNew(string path, string content)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
    }
    private static string HashFile(string path) => Hash(File.ReadAllBytes(Path.GetFullPath(
        Path.IsPathRooted(path) ? path : Path.Combine(Application.dataPath, "..", path))));
    private static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}

[DefaultExecutionOrder(12500)]
public sealed class ArdyIntentGeneralizationSampler : MonoBehaviour
{
    private void LateUpdate() => ArdyIntentGeneralizationRegression.AfterVrmFrame();
}
