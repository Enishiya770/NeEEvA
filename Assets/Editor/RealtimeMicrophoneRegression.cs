using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>Production microphone selection/lifecycle with a fake native device boundary.</summary>
public static class RealtimeMicrophoneRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<string> checks = new List<string>();

    [Serializable]
    private sealed class Report
    {
        public bool passed;
        public string scope = "Actual RTSpeechHandler device selection, manual push-to-talk, position and release with fake native microphone calls. No physical microphone, network, scene switch or headset activation.";
        public string error;
        public string[] checks;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("Run this entry in an isolated batch editor.");
        var report = new Report();
        try { Run(); report.passed = true; }
        catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks.ToArray();
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/realtime-microphone-regression.json", JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    public static int Run()
    {
        checks.Clear();
        WithProbe(probe =>
        {
            Check(Ensure(probe, out int position) && position == 320,
                "Desktop can read the system-default microphone position even with PICO listed first");
            Check(probe.Calls.Contains("start:<default>") && probe.Calls.Contains("position:<default>"),
                "Desktop Start and GetPosition both receive null");
            Check(probe.m_MicrophoneName == null,
                "Automatic selection leaves the serialized inspector preference blank");
            probe.ReStartRecord();
            Check(probe.Calls.Contains("end:<default>"), "Realtime shutdown ends the same null default device");
            int endCount = probe.EndCount;
            probe.NativeRecording = true; // An independent desktop push-to-talk now owns the device.
            probe.ReStartRecord();
            Check(probe.EndCount == endCount && probe.NativeRecording,
                "Repeated idle cleanup does not stop an independently owned desktop recording");
        });
        WithProbe(probe =>
        {
            probe.XrActive = true;
            Check(Ensure(probe, out _), "XR microphone is available");
            Check(probe.Calls.Contains("start:<default>") && probe.Calls.Contains("position:<default>"),
                "XR follows the system default when multiple legacy PICO inputs precede the AC Interface headset");
            Check(probe.m_MicrophoneName == null, "XR automatic selection keeps the inspector preference blank");
        });
        WithProbe(probe =>
        {
            probe.XrActive = true;
            Array.Reverse(probe.Devices);
            Check(Ensure(probe, out _) && probe.Calls.Contains("start:<default>"),
                "Reordering PICO and AC Interface endpoints does not change automatic XR input");
        });
        foreach (bool xrActive in new[] { false, true })
        {
            WithProbe(probe =>
            {
                probe.XrActive = xrActive;
                probe.m_MicrophoneName = " \t ";
                Check(Ensure(probe, out _) && probe.Calls.Contains("start:<default>") &&
                      probe.m_MicrophoneName == " \t ",
                    (xrActive ? "XR" : "Desktop") + " treats whitespace preference as system default without rewriting it");
            });
        }
        WithProbe(probe =>
        {
            probe.XrActive = true;
            probe.m_MicrophoneName = "Capture Input terminal (AC Interface)";
            Check(Ensure(probe, out _) &&
                  probe.Calls.Contains("start:Capture Input terminal (AC Interface)") &&
                  probe.Calls.Contains("position:Capture Input terminal (AC Interface)"),
                "An explicit AC Interface headset overrides system default and legacy PICO endpoints");
        });
        WithProbe(probe =>
        {
            probe.XrActive = true;
            Check(probe.BeginManualCapture() && probe.IsManualCaptureActive,
                "XR push-to-talk can begin a manual capture with automatic input");
            Check(probe.Calls.Contains("start:<default>") && probe.Calls.Contains("position:<default>") &&
                  !probe.Calls.Exists(call => call.IndexOf("Pico", StringComparison.OrdinalIgnoreCase) >= 0),
                "XR push-to-talk uses the same null default input despite registered legacy PICO devices");
            Check(Ensure(probe, out _) &&
                  probe.Calls.FindAll(call => call.StartsWith("start:", StringComparison.Ordinal)).Count == 1,
                "Refreshing the realtime listener during manual capture reuses its existing microphone session");
        });
        WithProbe(probe =>
        {
            probe.XrActive = true;
            probe.m_MicrophoneName = "USB microphone";
            Check(Ensure(probe, out _) && probe.Calls.Contains("start:USB microphone"),
                "An explicit input selection overrides the XR system default");
        });
        WithProbe(probe =>
        {
            probe.m_MicrophoneName = "Microphone (PicoStreamingMicrophone)";
            Check(Ensure(probe, out _) && probe.Calls.Contains("start:Microphone (PicoStreamingMicrophone)"),
                "An explicit PICO selection is preserved in desktop mode");
        });
        WithProbe(probe =>
        {
            probe.XrActive = true;
            probe.Devices = new[] { "USB microphone" };
            Check(Ensure(probe, out _) && probe.Calls.Contains("start:<default>"),
                "XR without a PICO input also follows the system default");
        });
        WithProbe(probe =>
        {
            probe.Devices = Array.Empty<string>();
            Check(!Ensure(probe, out _) && probe.Calls.Count == 0,
                "Missing input devices do not start or claim a default microphone");
        });
        WithProbe(probe =>
        {
            Check(Ensure(probe, out _), "Desktop capture starts before a mode change");
            Set(probe, "m_IsRecording", true);
            probe.XrActive = true;
            Check(Ensure(probe, out _) && probe.EndCount == 0,
                "Activating XR does not switch devices underneath an in-flight capture");
            Set(probe, "m_IsRecording", false);
            Check(Ensure(probe, out _) && probe.EndCount == 1 &&
                  probe.Calls.Contains("end:<default>") &&
                  probe.Calls.FindAll(call => call == "start:<default>").Count == 2,
                "The next idle boundary refreshes the device while keeping XR on system default");
            probe.XrActive = false;
            Check(Ensure(probe, out _) && probe.EndCount == 2 &&
                  probe.Calls[probe.Calls.Count - 1] == "position:<default>",
                "Returning to desktop keeps null default input");
        });
        WithProbe(probe =>
        {
            probe.Position = -1;
            Check(!Ensure(probe, out _), "Invalid native positions remain rejected for null default input");
        });
        Debug.Log("[RealtimeMicrophoneRegression] passed " + checks.Count + " checks");
        return checks.Count;
    }

    private static bool Ensure(RealtimeMicrophoneRegressionProbe probe, out int position)
    {
        object[] args = { -1 };
        bool result = (bool)typeof(RTSpeechHandler).GetMethod("TryEnsureMicrophone", Private).Invoke(probe, args);
        position = (int)args[0];
        return result;
    }

    private static void Set(object target, string field, object value) =>
        typeof(RTSpeechHandler).GetField(field, Private).SetValue(target, value);

    private static void WithProbe(Action<RealtimeMicrophoneRegressionProbe> action)
    {
        var go = new GameObject("Offline microphone fixture");
        go.SetActive(false); // Never run business Awake, Start, VAD, or audio upload.
        var probe = go.AddComponent<RealtimeMicrophoneRegressionProbe>();
        try { action(probe); }
        finally
        {
            foreach (AudioClip clip in probe.Clips) UnityEngine.Object.DestroyImmediate(clip);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
}

public sealed class RealtimeMicrophoneRegressionProbe : RTSpeechHandler
{
    public string[] Devices =
    {
        "Microphone (PicoStreamingMicrophone)",
        "Microphone Array (Pico Streaming Speaker)",
        "Microphone (Pico Streaming Virtual Audio)",
        "USB microphone",
        "Capture Input terminal (AC Interface)"
    };
    public bool XrActive;
    public bool NativeRecording;
    public int Position = 320;
    public int EndCount;
    public readonly List<string> Calls = new List<string>();
    public readonly List<AudioClip> Clips = new List<AudioClip>();
    protected override string[] GetMicrophoneDevices() => Devices;
    protected override bool IsMicrophoneXrActive() => XrActive;
    protected override bool MicrophoneIsRecording(string device)
    {
        Calls.Add("recording:" + (device ?? "<default>"));
        return NativeRecording;
    }
    protected override int GetMicrophonePosition(string device)
    {
        Calls.Add("position:" + (device ?? "<default>"));
        return Position;
    }
    protected override AudioClip StartMicrophone(string device)
    {
        Calls.Add("start:" + (device ?? "<default>"));
        NativeRecording = true;
        var clip = AudioClip.Create("Offline microphone samples", 16000, 1, 16000, false);
        Clips.Add(clip);
        return clip;
    }
    protected override void EndMicrophone(string device)
    {
        Calls.Add("end:" + (device ?? "<default>"));
        NativeRecording = false;
        EndCount++;
    }
}
