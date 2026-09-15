using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ArdyRoomStreamPureRegression
{
    [Serializable] private sealed class Report
    {
        public string status, error;
        public int speedChecks, trajectoryChecks;
    }
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only run in the isolated validation project.");
        var report = new Report();
        try
        {
            report.speedChecks = ArdyRoomFollowSpeedRegression.RunChecks();
            report.trajectoryChecks = ArdyRoomStreamTrajectoryRegression.RunChecks();
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-ardyRoomOutput");
        string output = index >= 0 ? args[index + 1] : "Logs/ardy-stream-pure";
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "stream-pure.json"), JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }
}
