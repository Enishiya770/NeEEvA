using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ArdyRoomDialogueSmokeRegression
{
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use the isolated validation project only.");
        try
        {
            AgentSpeechPhaseRegression.RunOrThrow();
            int roomTaskChecks = RoomTaskProtocolRegression.RunChecks();
            int targetChecks = ArdyRoomTargetBuilderRegression.RunChecks();
            ArdyRoomDialogueProtocolRegression.RunInteractive();
            string output = Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_OUTPUT");
            if (string.IsNullOrEmpty(output)) throw new ArgumentException("Missing evidence output.");
            File.WriteAllText(Path.Combine(output, "report.json"), "{\"status\":\"passed\",\"targetBuilderChecks\":" + targetChecks + ",\"roomTaskProtocolChecks\":" + roomTaskChecks + ",\"dialogueProtocolPassed\":true,\"speechPhasePassed\":true}");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
