using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Actual formal SSE deadlines on loopback; no real model, audio, avatar or action executor.</summary>
[InitializeOnLoad]
public static class FormalStreamRecoveryRegression
{
    private const string Key = "NeEEvA.FormalStreamRecovery.";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string PrivateMarker = "synthetic-private-output-8492";
    private const string PublicText = "現在の状態を確認したわ。";
    private const string PublicOutput = "<lang code=\"ja\"/><speech mode=\"independent\"/><say>" + PublicText + "</say>";
    private static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
    private static double started;
    private static int checks;
    public static JObject LastReport { get; private set; }
    private static string Output => SessionState.GetString(Key + "Output", "");

    static FormalStreamRecoveryRegression()
    {
        EditorApplication.update += Step;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                started = EditorApplication.timeSinceStartup;
                routines.Push(RunChecks());
            }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the isolated validation project, without -quit.");
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play already active.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a modified validation scene.");
        string output = "Tools/MotionAdapter/reports/formal-stream-recovery";
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; ++i)
            if (args[i] == "-formalStreamRecoveryOutput") output = args[i + 1];
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        SessionState.SetString(Key + "Output", output);
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        SessionState.SetBool(Key + "Passed", false);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.EnterPlaymode();
    }

    private static void Step()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false) || !EditorApplication.isPlaying || routines.Count == 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup - started > 90) throw new TimeoutException("Formal stream loopback suite timed out.");
            IEnumerator current = routines.Peek();
            if (!current.MoveNext())
            {
                (current as IDisposable)?.Dispose(); routines.Pop();
                if (routines.Count == 0) Finish(null);
            }
            else if (current.Current is IEnumerator nested) routines.Push(nested);
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        while (routines.Count > 0) (routines.Pop() as IDisposable)?.Dispose();
        if (LastReport == null) LastReport = new JObject();
        bool passed = error == null && (bool?)LastReport["passed"] == true;
        LastReport["passed"] = passed; LastReport["checks"] = checks;
        LastReport["checkedAtUtc"] = DateTime.UtcNow.ToString("o");
        LastReport["elapsedSeconds"] = EditorApplication.timeSinceStartup - started;
        if (error != null) { LastReport["error"] = error.ToString(); Debug.LogException(error); }
        File.WriteAllText(Path.Combine(Output, "report.json"), LastReport + "\n", new UTF8Encoding(false));
        SessionState.SetBool(Key + "Passed", passed); SessionState.SetBool(Key + "Finished", true);
        EditorApplication.ExitPlaymode();
    }

    private static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false);
        Debug.Log("[FormalStreamRecoveryRegression] passed=" + passed);
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);

    private static IEnumerator RunChecks()
    {
        checks = 0;
        LastReport = new JObject { ["passed"] = false, ["cases"] = new JArray(),
            ["scope"] = "Empty-scene Play Mode; production ChatQW public stage entrypoint and UnityWebRequest/SSE, synthetic loopback only. No real LLM, microphone, TTS, private prompt, avatar, navigation or device actions.",
            ["realModelRequests"] = 0, ["actionExecutorsCreated"] = 0 };
        PureChecks();
        checks += CommittedSpatialReplyProtocolRegression.RunChecks(Output);
        LastReport["committedPublicProtocol"] = CommittedSpatialReplyProtocolRegression.LastReport.DeepClone();
        ChatLatencyTransportRegression.RunInteractive();
        LastReport["existingLatencyTransportPassed"] = true;
        LastReport["existingPublicBoundaryChecks"] = RoleOutputPublicBoundaryRegression.RunOrThrow();
        LastReport["existingDecisionTransportChecks"] = RoomDecisionTransportRegression.RunChecks();
        checks += RoomDecisionRecoveryRegression.RunChecks();
        LastReport["decisionRecoveryPure"] = RoomDecisionRecoveryRegression.LastReport.DeepClone();
        checks += RoomDecisionBudgetRegression.RunChecks();
        LastReport["decisionBudgetPure"] = RoomDecisionBudgetRegression.LastReport.DeepClone();
        RoomDecisionBudgetRegression.ExportFixtures(Output);
        using (var fixture = new Fixture())
        {
            BudgetChecks(fixture.Model);
            yield return RunCase(fixture, "unclosed-thought-keeps-producing", new Script {
                Initial = Content("<thought>" + PrivateMarker + "<motion name=\"must-not-run\"/>"), Repeat = Content(" hidden"), RepeatCount = 80
            }, "deadline", false);
            yield return RunCase(fixture, "content-then-stalls", new Script {
                Initial = Content("<thought>" + PrivateMarker), PauseMilliseconds = 1100
            }, "delta-stalled", false);
            yield return RunCase(fixture, "reasoning-only-keeps-producing", new Script {
                Initial = Reasoning(PrivateMarker), Repeat = Reasoning(" hidden"), RepeatCount = 80
            }, "deadline", false);
            yield return RunCase(fixture, "keepalive-only", new Script {
                Initial = ": heartbeat\n\n", Repeat = ": heartbeat\n\n", RepeatCount = 80
            }, "deadline", false);
            yield return RunCase(fixture, "normal-silent-completion", new Script {
                Initial = Content("<silent/>"), FinishReason = "stop"
            }, null, false);
            yield return RunCase(fixture, "normal-public-completion", new Script {
                Initial = Content(PublicOutput), FinishReason = "stop"
            }, null, true);
            yield return RunCase(fixture, "private-prefix-then-public", new Script {
                Initial = Content("<thought>" + PrivateMarker), Next = Content("</thought>" + PublicOutput), NextDelayMilliseconds = 70, FinishReason = "stop"
            }, null, true);
            yield return RunCase(fixture, "leading-think-then-public", new Script {
                Initial = Content("<think>" + PrivateMarker), Next = Content("</think>" + PublicOutput), NextDelayMilliseconds = 70, FinishReason = "stop"
            }, null, true);
            yield return RunCase(fixture, "public-prefix-with-unfinished-private-tail", new Script {
                Initial = Content(PublicOutput + "<thought>" + PrivateMarker), Repeat = Content(" hidden"), RepeatCount = 80
            }, "deadline", true);
            yield return RunCase(fixture, "token-limit-before-public", new Script {
                Initial = Content("<thought>" + PrivateMarker + "<motion name=\"must-not-run\"/>"), FinishReason = "length"
            }, "incomplete-output", false);
            yield return RunCase(fixture, "token-limit-after-public-before-action-release", new Script {
                Initial = Content(PublicOutput + "<motion name=\"must-not-run\"/>"), FinishReason = "length"
            }, "incomplete-output", true);
            yield return RunCase(fixture, "normal-eof-without-finish-cannot-release-action", new Script {
                Initial = Content(PublicOutput + "<motion name=\"must-not-run\"/>")
            }, "incomplete-output", true);
            yield return RunCase(fixture, "http400-structured-error", new Script {
                StatusCode = 400,
                Initial = "{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"invalid_schema\",\"message\":\"Synthetic schema request rejected.\"}}"
            }, "http-failure", false);
            yield return RunCase(fixture, "public-prefix-then-truncated-http-body", new Script {
                Initial = Content(PublicOutput + "<motion name=\"must-not-run\"/>"),
                TruncateHttpBody = true, PauseMilliseconds = 100
            }, "http-failure", true);
            yield return CancelAndReplace(fixture);
            yield return RunCase(fixture, "committed-normal-public", new Script {
                Initial = Content(PublicOutput), FinishReason = "stop"
            }, null, true, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-explicit-silent", new Script {
                Initial = Content("<silent/>"), FinishReason = "stop"
            }, null, false, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-unclosed-thought-containing-say", new Script {
                Initial = Content("<thought>" + PrivateMarker + PublicOutput), FinishReason = "stop"
            }, "invalid-envelope", false, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-closed-thought-cannot-be-salvaged", new Script {
                Initial = Content("<thought>" + PrivateMarker + "</thought>" + PublicOutput), FinishReason = "stop"
            }, "invalid-envelope", false, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-public-prefix-then-tool-tail", new Script {
                Initial = Content(PublicOutput), Next = Content("<motion name=\"must-not-run\"/>"), NextDelayMilliseconds = 70, FinishReason = "stop"
            }, "invalid-envelope", false, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-public-prefix-then-truncated-stop", new Script {
                Initial = Content(PublicOutput), FinishReason = "length"
            }, "incomplete-output", false, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-public-prefix-then-truncated-http-body", new Script {
                Initial = Content(PublicOutput), TruncateHttpBody = true, PauseMilliseconds = 100
            }, "http-failure", false, FormalResponseStage.CommittedSpatialReply);
            yield return RunCase(fixture, "committed-never-finishes-thought", new Script {
                Initial = Content("<thought>" + PrivateMarker), Repeat = Content(" hidden"), RepeatCount = 80
            }, "deadline", false, FormalResponseStage.CommittedSpatialReply);
            yield return CancelAndReplace(fixture, FormalResponseStage.CommittedSpatialReply);
            LastReport["loopbackRequests"] = fixture.Server.Requests;
        }
        yield return RoomDecisionRecoveryRegression.RunHttpChecks();
        Check((string)RoomDecisionRecoveryRegression.HttpReport?["status"] == "passed", "Room decision HTTP recovery did not pass.");
        LastReport["decisionRecoveryHttp"] = RoomDecisionRecoveryRegression.HttpReport.DeepClone();
        yield return SpatialGoalResultTransportRegression.RunChecks(Output);
        Check((string)SpatialGoalResultTransportRegression.LastReport?["status"] == "passed", "Spatial result and reference HTTP boundaries did not pass.");
        LastReport["spatialResultTransport"] = SpatialGoalResultTransportRegression.LastReport.DeepClone();
        LastReport["passed"] = true; LastReport["checks"] = checks;
    }

    private static void PureChecks()
    {
        var channels = new RoleOutputChannels(null, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        channels.Push("<thought>" + PrivateMarker + "<motion name=\"must-not-run\"/>");
        JObject first = channels.ProgressSnapshot();
        Check((int)first["thoughtDepth"] == 1 && (int)first["privateCharacters"] > 0 && !channels.HasSpeech && !channels.HasActions,
            "Progress did not identify an open private thought without dispatching nested tags.");
        Check(!first.ToString().Contains(PrivateMarker) && !first.ToString().Contains("must-not-run"), "Parser progress exposed private content.");
        Check(JToken.DeepEquals(first, channels.ProgressSnapshot()), "Progress inspection mutated the parser.");
        channels.Push("</thought>" + PublicOutput); channels.Finish();
        Check(channels.Speech == PublicText && !channels.HasInvalidPublicBoundary && !channels.HasActions,
            "A bounded private prefix prevented a valid public continuation.");
        var silent = new RoleOutputChannels(null, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        silent.Push("<silent/>");
        Check(silent.ExplicitlySilent && (bool)silent.ProgressSnapshot()["explicitlySilent"] && !silent.HasSpeech, "Explicit silent shape was lost.");
        var tag = new RoleOutputChannels(null, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        tag.Push("<say");
        Check((int)tag.ProgressSnapshot()["pendingTagCharacters"] == 4, "Partial tag was invisible to diagnostics.");
        var code = new RoleOutputChannels(null, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        code.Push("`" + PrivateMarker);
        Check((bool)code.ProgressSnapshot()["insideCode"] && !code.ProgressSnapshot().ToString().Contains(PrivateMarker), "Code-state diagnostic leaked text.");
        var progress = new FormalStreamProgress();
        progress.Receive(52); progress.Event(); progress.Delta("abc", PrivateMarker); progress.ParseFailure(); progress.MarkDone();
        JObject wire = progress.Snapshot(7);
        Check((long)wire["contentCharacters"] == 3 && (long)wire["reasoningCharacters"] == PrivateMarker.Length &&
            (int)wire["sseEvents"] == 1 && (int)wire["parseFailures"] == 1 && (bool)wire["doneReceived"] &&
            (int)wire["pendingLineCharacters"] == 7 && !wire.ToString().Contains(PrivateMarker), "Wire progress counts or privacy failed.");
        Check(FormalStreamProgress.Deadline(31, 30, false, 30, 15, 90) == "no-public-output-deadline", "Private token activity bypassed public deadline.");
        Check(FormalStreamProgress.Deadline(20, 1, true, 30, 15, 90) == "delta-stalled", "First speech disabled idle deadline.");
        Check(FormalStreamProgress.Deadline(91, 90, true, 30, 15, 90) == "total-deadline", "Continuous tokens disabled total deadline.");
        Check(FormalStreamProgress.Deadline(10, 9, false, 30, 15, 90) == null, "Normal private reasoning was rejected before a deadline.");
    }

    private static void BudgetChecks(ChatQW model)
    {
        model.m_EnableThinking = true;
        var history = new List<LLM.SendData> { new LLM.SendData("system", "Synthetic persona."), new LLM.SendData("user", "Explain current facts.") };
        JObject Build(FormalResponseStage stage) => JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
            .Invoke(model, new object[] { history, true, "Synthetic current facts: no movement was submitted.", null, stage }));
        JObject committed = Build(FormalResponseStage.CommittedSpatialReply), general = Build(FormalResponseStage.General);
        Check((int?)committed["max_tokens"] == CommittedSpatialReplyProtocol.MaximumOutputTokens &&
            (string)committed["grammar"] == CommittedSpatialReplyProtocol.OutputGrammar && (bool?)committed["enable_thinking"] == false &&
            (bool?)committed["chat_template_kwargs"]?["enable_thinking"] == false, "Committed wire did not bound/disable thinking.");
        Check((bool?)general["enable_thinking"] == true && general["max_tokens"] == null && model.m_EnableThinking,
            "Per-request committed budget changed General or the shared provider setting.");
        model.m_EnableThinking = false;
    }

    private static IEnumerator RunCase(Fixture fixture, string name, Script script, string expectedFailure, bool expectedSpeech,
        FormalResponseStage stage = FormalResponseStage.General)
    {
        fixture.Reset();
        var observation = new Observation();
        Action<string> failure = value => observation.Failures.Add(value);
        Action<SystemNotice> notice = value => observation.Notices.Add(value.Code);
        Application.LogCallback log = (message, stack, type) => {
            const string prefix = "[LLM/StreamProgress] ";
            if (message.StartsWith(prefix, StringComparison.Ordinal)) observation.Progress.Add(JObject.Parse(message.Substring(prefix.Length)));
            const string httpPrefix = "[LLM/StreamHttpFailure] ";
            if (message.StartsWith(httpPrefix, StringComparison.Ordinal)) observation.HttpFailures.Add(JObject.Parse(message.Substring(httpPrefix.Length)));
        };
        fixture.Model.OnResponseGenerationFailure += failure; fixture.Model.OnSystemNotice += notice;
        Application.logMessageReceived += log;
        Exchange exchange = fixture.Server.Serve(script);
        double start = EditorApplication.timeSinceStartup;
        try
        {
            fixture.Model.PostSpeechStreamForStage("Synthetic request: explain current result.", part => observation.Speech.Append(part.Text),
                text => { observation.Callbacks++; observation.Completion = text; }, null, true, stage);
            yield return WaitFor(() => observation.Callbacks > 0, exchange, name, 3);
            yield return Delay(.08);
            Check(observation.Callbacks == 1, name + ": completion callback was not unique.");
            Check(exchange.Wire != null, name + ": request did not reach real loopback HTTP.");
            JObject request = JObject.Parse(exchange.Wire);
            Check(stage == FormalResponseStage.CommittedSpatialReply
                ? (int?)request["max_tokens"] == CommittedSpatialReplyProtocol.MaximumOutputTokens &&
                    (string)request["grammar"] == CommittedSpatialReplyProtocol.OutputGrammar && (bool?)request["enable_thinking"] == false
                : request["max_tokens"] == null && request["grammar"] == null,
                name + ": per-request public protocol leaked between General and committed stages.");
            Check((observation.Speech.Length > 0) == expectedSpeech && !observation.Speech.ToString().Contains(PrivateMarker) &&
                !observation.Speech.ToString().Contains("must-not-run"), name + ": private text/action escaped as public speech.");
            Check(!(observation.Completion ?? "").Contains("motion") && !(observation.Completion ?? "").Contains(PrivateMarker),
                name + ": incomplete private output escaped as executable completion.");
            if (expectedFailure == null)
            {
                Check(observation.Failures.Count == 0 && observation.Notices.Count == 0, name + ": valid completion was treated as failure.");
                Check(expectedSpeech ? observation.Speech.ToString() == PublicText && observation.Completion.Contains(PublicText) : observation.Completion == "<silent/>",
                    name + ": normal public/silent completion changed.");
                Check(fixture.Model.m_DataList.Exists(m => m.role == "assistant"), name + ": successful accepted response was not recorded.");
            }
            else if (expectedFailure == "incomplete-output" || expectedFailure == "invalid-envelope")
            {
                Check(observation.Failures.Count == 1 && observation.Failures[0] == "incomplete-output" &&
                    observation.Notices.Count == 1 && observation.Notices[0] ==
                        (expectedFailure == "invalid-envelope" ? "llm_output_invalid" : "llm_output_truncated"),
                    name + ": incomplete generation did not emit one classified failure and notice.");
                Check(observation.Completion == "" && !fixture.Model.m_DataList.Exists(m => m.role == "assistant"),
                    name + ": incomplete output became completed assistant history.");
                Check(!fixture.Model.m_DataList.Exists(m => m.content.Contains(PrivateMarker) || m.content.Contains("must-not-run")),
                    name + ": incomplete private/action tail leaked into history.");
            }
            else if (expectedFailure == "http-failure")
            {
                Check(observation.Failures.Count == 1 && observation.Failures[0] == "http-failure" &&
                    observation.Notices.Count == 1 && observation.Notices[0] == "llm_response_failed" && observation.Completion == "",
                    name + ": HTTP failure did not emit one classified failure, notice and empty callback.");
                Check(!fixture.Model.m_DataList.Exists(m => m.role == "assistant" || m.content.Contains(PrivateMarker) || m.content.Contains("must-not-run")),
                    name + ": incomplete HTTP content became accepted assistant/action history.");
                Check(observation.HttpFailures.Count == 1 && !observation.HttpFailures.ToString().Contains("must-not-run") &&
                    !observation.HttpFailures.ToString().Contains(PublicText), name + ": HTTP error evidence missing or exposed partial SSE text.");
                if (script.StatusCode == 400)
                    Check(observation.HttpFailures.ToString().Contains("invalid_schema"), name + ": structured provider error was discarded.");
            }
            else
            {
                Check(observation.Failures.Count == 1 && (expectedFailure == "deadline" ? observation.Failures[0].EndsWith("deadline", StringComparison.Ordinal) : observation.Failures[0] == expectedFailure),
                    name + ": no unique classified generation failure.");
                Check(observation.Notices.Count == 1 && observation.Notices[0] == "llm_response_deadline" && observation.Completion == "",
                    name + ": deadline did not produce one notice and an empty completion.");
                Check(!fixture.Model.m_DataList.Exists(m => m.role == "assistant" || m.content.Contains(PrivateMarker) || m.content.Contains("must-not-run")),
                    name + ": partial content was accepted into history.");
                Check(observation.Progress.Count > 0 && !observation.Progress.ToString().Contains(PrivateMarker), name + ": deadline diagnostic missing or exposed text.");
                JObject finalProgress = (JObject)observation.Progress[observation.Progress.Count - 1];
                if (name == "reasoning-only-keeps-producing") Check((long)finalProgress["wire"]["reasoningCharacters"] > 0 && (long)finalProgress["wire"]["contentCharacters"] == 0,
                    "Reasoning-only stream was indistinguishable from content.");
                if (name == "unclosed-thought-keeps-producing") Check((int)finalProgress["channels"]["thoughtDepth"] == 1 && (long)finalProgress["wire"]["contentCharacters"] > 0,
                    "Unclosed-thought diagnostic did not preserve its parser cause.");
            }
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = name, ["passed"] = true,
                ["elapsedSeconds"] = EditorApplication.timeSinceStartup - start, ["callbacks"] = observation.Callbacks,
                ["failures"] = new JArray(observation.Failures), ["notices"] = new JArray(observation.Notices),
                ["speechCharacters"] = observation.Speech.Length, ["completionCharacters"] = observation.Completion?.Length ?? 0,
                ["progress"] = observation.Progress, ["httpFailures"] = observation.HttpFailures });
        }
        finally
        {
            fixture.Model.OnResponseGenerationFailure -= failure; fixture.Model.OnSystemNotice -= notice;
            Application.logMessageReceived -= log;
            fixture.Model.CancelActiveResponse(); exchange.Stop.Cancel();
        }
    }

    private static IEnumerator CancelAndReplace(Fixture fixture, FormalResponseStage stage = FormalResponseStage.General)
    {
        fixture.Reset();
        var old = new Observation(); var current = new Observation();
        int failures = 0, notices = 0;
        Action<string> failure = _ => failures++; Action<SystemNotice> notice = _ => notices++;
        fixture.Model.OnResponseGenerationFailure += failure; fixture.Model.OnSystemNotice += notice;
        Exchange obsolete = fixture.Server.Serve(new Script { Initial = Content("<thought>" + PrivateMarker), Repeat = Content(" old"), RepeatCount = 80 });
        Exchange replacement = null;
        try
        {
            fixture.Model.PostSpeechStreamForStage("Old synthetic user request.", part => old.Speech.Append(part.Text),
                text => { old.Callbacks++; old.Completion = text; }, null, true, stage);
            yield return WaitFor(() => obsolete.SentEvents > 0, obsolete, "cancel-start", 2);
            yield return Delay(.06);
            fixture.Model.CancelActiveResponse();
            replacement = fixture.Server.Serve(new Script { Initial = Content(PublicOutput), FinishReason = "stop" });
            fixture.Model.PostSpeechStreamForStage("Replacement synthetic user request.", part => current.Speech.Append(part.Text),
                text => { current.Callbacks++; current.Completion = text; }, null, true, stage);
            yield return WaitFor(() => current.Callbacks > 0, replacement, "cancel-replacement", 3);
            // Wait beyond the old request's deadline while its producer keeps attempting events.
            yield return Delay(.65);
            Check(old.Callbacks == 0 && old.Speech.Length == 0 && failures == 0 && notices == 0,
                "Cancelled generation published callback, old speech or failure into replacement.");
            Check(current.Callbacks == 1 && current.Speech.ToString() == PublicText && current.Completion.Contains(PublicText),
                "Replacement request did not complete exactly once.");
            Check(fixture.Model.m_DataList.FindAll(m => m.role == "assistant").Count == 1 &&
                !fixture.Model.m_DataList.Exists(m => m.content.Contains(PrivateMarker)), "Cancelled private stream contaminated accepted history.");
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = "user-cancel-and-replacement-" + stage, ["passed"] = true,
                ["oldCallbacks"] = old.Callbacks, ["newCallbacks"] = current.Callbacks, ["failureEvents"] = failures,
                ["notices"] = notices, ["newSpeechCharacters"] = current.Speech.Length });
        }
        finally
        {
            fixture.Model.OnResponseGenerationFailure -= failure; fixture.Model.OnSystemNotice -= notice;
            fixture.Model.CancelActiveResponse(); obsolete.Stop.Cancel(); replacement?.Stop.Cancel();
        }
    }

    private static IEnumerator Delay(double seconds)
    {
        double end = EditorApplication.timeSinceStartup + seconds;
        while (EditorApplication.timeSinceStartup < end) yield return null;
    }

    private static IEnumerator WaitFor(Func<bool> ready, Exchange exchange, string name, double seconds)
    {
        double end = EditorApplication.timeSinceStartup + seconds;
        while (!ready())
        {
            if (exchange.Task.IsFaulted) throw exchange.Task.Exception;
            if (EditorApplication.timeSinceStartup > end) throw new TimeoutException("Loopback condition timed out: " + name);
            yield return null;
        }
    }

    private sealed class Observation
    {
        public int Callbacks;
        public string Completion;
        public readonly StringBuilder Speech = new StringBuilder();
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Notices = new List<string>();
        public readonly JArray Progress = new JArray();
        public readonly JArray HttpFailures = new JArray();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly LoopbackServer Server = new LoopbackServer();
        public readonly GameObject Host;
        public readonly ChatQW Model;
        public Fixture()
        {
            Host = new GameObject("SyntheticFormalStreamRecovery"); Host.SetActive(false);
            Model = Host.AddComponent<ChatQW>(); Model.enabled = false;
            Model.m_Backend = ChatQW.BackendType.Local; Model.m_LocalUrl = Server.Url;
            Model.m_PrewarmPrefixOnStart = false; Model.m_LogRequestStats = false;
            typeof(LLM).GetField("url", Flags).SetValue(Model, Server.Url);
            Set(Model, "m_StreamFirstPublicOutputSeconds", .55f); Set(Model, "m_StreamIdleSeconds", .25f); Set(Model, "m_StreamTotalSeconds", 1.1f);
            Host.SetActive(true);
            Check((string)typeof(LLM).GetField("url", Flags).GetValue(Model) == Server.Url, "Fixture selected a non-loopback endpoint.");
            Reset();
        }
        public void Reset()
        {
            Model.CancelActiveResponse(); Model.m_DataList.Clear();
            Model.m_DataList.Add(new LLM.SendData("system", "Synthetic persona. Use Japanese public speech only."));
            Model.RequestContext = "[Synthetic current fact] No spatial action was submitted. This request describes known facts only.";
        }
        public void Dispose()
        {
            Model.CancelActiveResponse(); UnityEngine.Object.DestroyImmediate(Host); Server.Dispose();
        }
    }

    private sealed class Script
    {
        public string Initial, Next, Repeat, FinishReason;
        public int NextDelayMilliseconds, RepeatCount, PauseMilliseconds;
        public int StatusCode = 200;
        public bool TruncateHttpBody;
    }

    private sealed class Exchange
    {
        public readonly CancellationTokenSource Stop = new CancellationTokenSource();
        public Task Task;
        public volatile string Wire;
        public volatile int SentEvents;
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly List<Exchange> exchanges = new List<Exchange>();
        public string Url { get; }
        public int Requests;
        public LoopbackServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
        }
        public Exchange Serve(Script script)
        {
            var exchange = new Exchange(); exchanges.Add(exchange);
            exchange.Task = Task.Run(async () => {
                var context = await listener.GetContextAsync();
                Interlocked.Increment(ref Requests);
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) exchange.Wire = await reader.ReadToEndAsync();
                context.Response.StatusCode = script.StatusCode;
                context.Response.ContentType = script.StatusCode == 200 ? "text/event-stream" : "application/json";
                context.Response.SendChunked = !script.TruncateHttpBody;
                if (script.TruncateHttpBody)
                {
                    context.Response.ContentLength64 = Encoding.UTF8.GetByteCount(script.Initial ?? "") + 4096;
                    context.Response.KeepAlive = false;
                }
                try
                {
                    async Task Send(string value)
                    {
                        if (string.IsNullOrEmpty(value)) return;
                        byte[] bytes = Encoding.UTF8.GetBytes(value);
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, exchange.Stop.Token);
                        await context.Response.OutputStream.FlushAsync(exchange.Stop.Token);
                        exchange.SentEvents++;
                    }
                    await Send(script.Initial);
                    if (script.NextDelayMilliseconds > 0) await Task.Delay(script.NextDelayMilliseconds, exchange.Stop.Token);
                    await Send(script.Next);
                    for (int i = 0; i < script.RepeatCount; ++i)
                    {
                        await Task.Delay(25, exchange.Stop.Token); await Send(script.Repeat);
                    }
                    if (script.PauseMilliseconds > 0) await Task.Delay(script.PauseMilliseconds, exchange.Stop.Token);
                    if (script.FinishReason != null)
                        await Send("data: " + new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject(), ["finish_reason"] = script.FinishReason }) }.ToString(Newtonsoft.Json.Formatting.None) + "\n\ndata: [DONE]\n\n");
                }
                catch (OperationCanceledException) { }
                catch (IOException) { if (script.FinishReason != null) throw; }
                catch (HttpListenerException) { if (script.FinishReason != null) throw; }
                finally
                {
                    if (script.TruncateHttpBody) context.Response.Abort();
                    else context.Response.Close();
                }
            });
            return exchange;
        }
        public void Dispose()
        {
            foreach (var exchange in exchanges) exchange.Stop.Cancel();
            listener.Stop(); listener.Close();
        }
    }

    private static string Content(string text) => Delta("content", text);
    private static string Reasoning(string text) => Delta("reasoning_content", text);
    private static string Delta(string field, string text) => "data: " + new JObject {
        ["choices"] = new JArray(new JObject { ["delta"] = new JObject { [field] = text } })
    }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
}
