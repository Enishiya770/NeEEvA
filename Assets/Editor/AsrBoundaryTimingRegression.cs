using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>Offline production recorder tests; no native microphone, model, service or scene.</summary>
public static class AsrBoundaryTimingRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<string> checks = new List<string>();

    [Serializable] private sealed class Report
    {
        public bool passed;
        public string scope = "Production EOU admission/timing/version checks and exact PCM pre-roll across ring wrap. No real microphone or ASR accuracy claim.";
        public string error;
        public string[] checks;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch editor.");
        var report = new Report();
        try { Run(); report.passed = true; }
        catch (Exception e) { report.error = e.ToString(); Debug.LogException(e); }
        report.checks = checks.ToArray();
        Directory.CreateDirectory("Logs/asr");
        File.WriteAllText("Logs/asr/boundary-timing-regression.json", JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    public static int Run()
    {
        checks.Clear();
        Admission(true, .8f, 0f, 0f, true, "initial-measurement");
        Admission(true, .8f, 2.5f, 5f, false, "slower-than-remaining-quiet");
        Admission(true, .8f, .2f, 5f, true, "fits-quiet-budget");
        Admission(true, .29f, .2f, 5f, false, "slower-than-remaining-quiet");
        Admission(true, 0f, 0f, 0f, false, "capture-close-due");
        Admission(true, -.1f, 2.5f, 31f, false, "capture-close-due");
        Admission(true, .8f, 2.5f, 31f, true, "latency-refresh-probe");
        Admission(false, -.1f, 7f, 0f, true, "not-covered-quiet");
        // Four covered quiet turns inside the probe interval: only the initial
        // unknown request starts. This is a deterministic budget simulation,
        // not a counterfactual claim about the recorded real session.
        int requests = 0;
        float observed = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (Admitted(true, .8f, observed, i * 5f, out _)) { requests++; observed = .8f; }
        }
        Check(requests == 1, "Four covered quiet turns start one remote request after a cancelled lower bound");
        WithHandler(handler =>
        {
            Set(handler, "m_StalledTranscriptReviewRequested", true);
            Set(handler, "m_StalledReviewRemoteAllowed", false);
            Set(handler, "m_StalledTranscriptReviewRequestTime", 10f);
            Call(handler, "ObserveBoundaryRequestDuration", 12f, true);
            Check(Get<float>(handler, "m_ObservedBoundarySeconds") == 0f,
                "Prepared-only result never trains remote latency");
            Set(handler, "m_StalledReviewRemoteAllowed", true);
            Call(handler, "ObserveBoundaryRequestDuration", 12f, true);
            Check(Get<float>(handler, "m_ObservedBoundarySeconds") == 0f,
                "Even a slow synchronous prepared callback is excluded from remote latency");
            Set(handler, "m_BoundaryRemoteTimingArmed", true);
            Set(handler, "m_BoundaryRequestTimingRecorded", false);
            Call(handler, "ObserveBoundaryRequestDuration", 10.8f, false);
            Check(Near(Get<float>(handler, "m_ObservedBoundarySeconds"), .8f),
                "Cancelled pending request contributes only its elapsed lower bound");
            Call(handler, "ObserveBoundaryRequestDuration", 13f, false);
            Check(Near(Get<float>(handler, "m_ObservedBoundarySeconds"), .8f),
                "Stop plus reset cannot double count a cancelled request");
            Set(handler, "m_BoundaryRequestTimingRecorded", false);
            Call(handler, "ObserveBoundaryRequestDuration", 10.4f, false);
            Check(Near(Get<float>(handler, "m_ObservedBoundarySeconds"), .8f),
                "A shorter cancellation never claims backend speed recovery");
            Set(handler, "m_BoundaryRequestTimingRecorded", false);
            Call(handler, "ObserveBoundaryRequestDuration", 10.4f, true);
            Check(Near(Get<float>(handler, "m_ObservedBoundarySeconds"), .7f),
                "A completed response can lower the estimate gradually");
            Set(handler, "m_BoundaryRequestTimingRecorded", false);
            Call(handler, "ObserveBoundaryRequestDuration", 20f, false);
            Check(Near(Get<float>(handler, "m_ObservedBoundarySeconds"), 7f),
                "Watchdog request contributes a bounded seven second lower bound");
            Call(handler, "ResetStalledTurnState");
            Check(Near(Get<float>(handler, "m_ObservedBoundarySeconds"), 7f),
                "New capture preserves latency observations");
        });
        FreshnessChecks();
        RingChecks();
        Debug.Log("[AsrBoundaryTimingRegression] passed " + checks.Count + " checks");
        return checks.Count;
    }

    private static void FreshnessChecks()
    {
        WithHandler(handler =>
        {
            var activity = Get<TurnActivityEvidence>(handler, "m_TurnActivity");
            activity.Reset(10f);
            Observe(activity, "先说", 1000, 10.1f);
            Review(handler, activity, "先说");
            Observe(activity, "先说", 1300, 10.4f);
            Set(handler, "m_LatestStreamPartial", "先 说。 ");
            Receive(handler, "先说");
            Check(Get<bool>(handler, "m_PendingSemanticEou"),
                "Unchanged normalized partial and fresh audio coverage preserve a semantic decision");
            Check(activity.ActivityRevision == 0 && activity.Revision == 1,
                "ASR words increment text revision without inventing a sound onset");
        });
        WithHandler(handler =>
        {
            var activity = Get<TurnActivityEvidence>(handler, "m_TurnActivity");
            activity.Reset(10f);
            Observe(activity, "先说", 1000, 10.1f);
            Review(handler, activity, "先说");
            Observe(activity, "别说", 1300, 10.4f);
            Set(handler, "m_LatestStreamPartial", "别说");
            Receive(handler, "先说");
            Check(!Get<bool>(handler, "m_PendingSemanticEou") && activity.PendingText,
                "Unconfirmed one-character semantic repair rejects the old decision");
            Observe(activity, "别说", 1600, 10.7f);
            Observe(activity, "先说", 1900, 11f);
            Observe(activity, "先说", 2200, 11.3f);
            Set(handler, "m_LatestStreamPartial", "先说");
            Receive(handler, "先说");
            Check(!Get<bool>(handler, "m_PendingSemanticEou") && !activity.PendingText &&
                  activity.ActivityRevision == 0 && activity.Revision == 3,
                "Text changed away and back cannot revive an older decision even without new sound");
        });
        WithHandler(handler =>
        {
            var activity = Get<TurnActivityEvidence>(handler, "m_TurnActivity");
            activity.Reset(10f);
            for (int i = 0; i < 20; i++) activity.ObserveLevel(.05f, 10f + i * .06f, .01f, .003f);
            Observe(activity, "啦", 1000, 11.2f);
            Review(handler, activity, "啦");
            int onset = activity.ActivityRevision;
            for (int i = 20; i < 30; i++) activity.ObserveLevel(.05f, 10f + i * .06f, .01f, .003f);
            Receive(handler, "啦");
            Check(activity.ActivityRevision == onset && !Get<bool>(handler, "m_PendingSemanticEou"),
                "Continuing vowel rejects take-turn even when onset revision and words stay identical");
        });
    }

    private static void RingChecks()
    {
        WithHandler(handler =>
        {
            Check(Near(handler.m_RecordingTimeLimit, 1.5f) && Near(handler.m_SingingEouSilence, 1.8f) &&
                  Near(handler.m_PostPlaybackEchoGuardSeconds, 1.2f),
                "Speech/singing quiet thresholds and post-playback echo guard retain their defaults");
            var clips = new List<AudioClip>();
            try
            {
                var ring = AudioClip.Create("Offline ring sentinel", 16000, 1, 16000, false);
                clips.Add(ring);
                var pcm = new float[16000];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = i / 32768f;
                Check(ring.SetData(pcm, 0), "Sentinel PCM can be written into the fake ring");
                Set(handler, "m_RecordedClip", ring);
                var startMethod = typeof(RTSpeechHandler).GetMethod("CalculateRecordingStartPos", Instance,
                    null, new[] { typeof(int), typeof(float) }, null);
                int start = (int)startMethod.Invoke(handler, new object[] { 1600, .25f });
                Check(start == 13600, "A quarter-second pre-roll crosses the ring boundary without losing its start");
                var snapshot = (AudioClip)Call(handler, "SnapshotFromBuffer", start, 1600);
                clips.Add(snapshot);
                CheckPcm(snapshot, pcm, start, 4000, "Pre-roll snapshot retains every requested sample across wrap");
                // Recognition/VAD callback arrives later. The accepted candidate's
                // saved start stays fixed while the device position advances.
                Call(handler, "BeginRecordingCapture", start, 4800);
                var preview = (AudioClip)Call(handler, "BuildAccumulatedRecordingClip", false);
                clips.Add(preview);
                CheckPcm(preview, pcm, start, 7200, "Delayed VAD uses the original candidate start including pre-roll");
                Check(Get<int>(handler, "m_RecordingCapturedFrames") == 7200,
                    "Preview does not consume original audio needed by final ASR");
                Call(handler, "CaptureRecordingAudio", 8000, true);
                var final = (AudioClip)Call(handler, "BuildAccumulatedRecordingClip", true);
                clips.Add(final);
                CheckPcm(final, pcm, start, 10400, "Final clip retains both pre-roll and the complete added tail");
                Check(Get<int>(handler, "m_RecordingCapturedFrames") == 0,
                    "Final ownership consumes the accumulation once");
                Check(handler.m_PostPlaybackEchoGuardSeconds > .25f,
                    "Test explicitly does not claim quarter-second pre-roll recovers early speech throughout the guard");
            }
            finally
            {
                Set(handler, "m_RecordedClip", null);
                foreach (AudioClip clip in clips) if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
            }
        });
    }

    private static void CheckPcm(AudioClip clip, float[] ring, int start, int count, string name)
    {
        Check(clip != null && clip.samples == count && clip.frequency == 16000 && clip.channels == 1, name + " / shape");
        var actual = new float[count];
        Check(clip.GetData(actual, 0), name + " / readable");
        for (int i = 0; i < count; i++)
            if (!Near(actual[i], ring[(start + i) % ring.Length])) throw new InvalidOperationException(name + " at sample " + i);
        Check(true, name + " / exact samples");
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .00001f;
    private static void Observe(TurnActivityEvidence activity, string text, int audioMs, float now) =>
        activity.ObserveAsr(text, "", audioMs, now, true, 0f, 0f, 0f, .005f, true, 0, -1, now);
    private static void Review(RTSpeechHandler handler, TurnActivityEvidence activity, string text)
    {
        Set(handler, "m_IsRecording", true);
        Set(handler, "m_LatestStreamPartial", text);
        Set(handler, "m_StalledReviewActivityRevision", activity.ActivityRevision);
        Set(handler, "m_StalledReviewTextRevision", activity.Revision - activity.ActivityRevision);
        Set(handler, "m_StalledReviewSoundAt", activity.LatestSoundAt);
    }
    private static void Receive(RTSpeechHandler handler, string text) =>
        Call(handler, "HandleStalledUserTurnDecision", "take_turn", .9f, "speech", .9f, "user", text, 1000);
    private static void Admission(bool covered, float remaining, float observed, float age, bool expected, string expectedReason)
    {
        bool actual = Admitted(covered, remaining, observed, age, out string reason);
        Check(actual == expected && reason == expectedReason, "Admission " + expectedReason + " / " + remaining + "/" + observed + "/" + age);
    }
    private static bool Admitted(bool covered, float remaining, float observed, float age, out string reason)
    {
        object[] args = { covered, remaining, observed, age, null };
        bool result = (bool)typeof(RTSpeechHandler).GetMethod("ShouldStartRemoteBoundaryReview", Static).Invoke(null, args);
        reason = (string)args[4]; return result;
    }
    private static void WithHandler(Action<RTSpeechHandler> action)
    {
        var host = new GameObject("Offline ASR timing fixture");
        host.SetActive(false);
        var handler = host.AddComponent<RTSpeechHandler>();
        try { action(handler); }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
    private static T Get<T>(RTSpeechHandler handler, string field) => (T)typeof(RTSpeechHandler).GetField(field, Instance).GetValue(handler);
    private static void Set(RTSpeechHandler handler, string field, object value) => typeof(RTSpeechHandler).GetField(field, Instance).SetValue(handler, value);
    private static object Call(RTSpeechHandler handler, string method, params object[] args) => typeof(RTSpeechHandler).GetMethod(method, Instance).Invoke(handler, args);
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
}
