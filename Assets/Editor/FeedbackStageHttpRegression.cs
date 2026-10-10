using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Focused Play Mode runner for five synthetic feedback requests; no room, avatar or services.</summary>
[InitializeOnLoad]
public static class FeedbackStageHttpRegression
{
    private const string Key = "NeEEvA.FeedbackStage.Http.";
    private static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
    private static double started;
    private static string Output => SessionState.GetString(Key + "Output", "");

    static FeedbackStageHttpRegression()
    {
        EditorApplication.update += Step;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                started = EditorApplication.timeSinceStartup;
                routines.Push(ChatLatencyTransportRegression.RunFeedbackStageHttpChecks(Output));
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
        string output = "Tools/MotionAdapter/reports/feedback-stage-http-focus";
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; ++i)
            if (args[i] == "-feedbackStageOutput") output = args[i + 1];
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
            if (EditorApplication.timeSinceStartup - started > 45) throw new TimeoutException("Focused feedback-stage loopback test timed out.");
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
        bool passed = error == null && ChatLatencyTransportRegression.FeedbackStageHttpReport != null &&
            (bool)ChatLatencyTransportRegression.FeedbackStageHttpReport["passed"];
        var report = new JObject { ["passed"] = passed, ["checkedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["elapsedSeconds"] = EditorApplication.timeSinceStartup - started,
            ["feedbackStageHttpEvidence"] = ChatLatencyTransportRegression.FeedbackStageHttpReport,
            ["scope"] = "Focused empty-scene Play Mode. Actual ChatQW feedback HTTP/SSE on synthetic loopback only; no real room, avatar, LLM, TTS or scene baseline." };
        if (error != null) { report["error"] = error.ToString(); Debug.LogException(error); }
        File.WriteAllText(Path.Combine(Output, "report.json"), report.ToString() + "\n");
        SessionState.SetBool(Key + "Passed", passed); SessionState.SetBool(Key + "Finished", true);
        EditorApplication.ExitPlaymode();
    }

    private static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false);
        Debug.Log("[FeedbackStageHttpRegression] passed=" + passed);
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
