using System;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEngine;

/// <summary>Native desktop windows must not outlive Play Mode or their managed owners.</summary>
[InitializeOnLoad]
public static class CompanionDesktopSubtitlesLifecycle
{
    static CompanionDesktopSubtitlesLifecycle()
    {
        AssemblyReloadEvents.beforeAssemblyReload += CloseWindows;
        EditorApplication.quitting += CloseWindows;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                CloseWindows();
        };
    }

    private static void CloseWindows()
    {
        try { CompanionDesktopSubtitles.DisposeAll(); }
        catch (Exception error) { Debug.LogWarning("Desktop subtitles could not finish closing: " + error.Message); }
    }
}
