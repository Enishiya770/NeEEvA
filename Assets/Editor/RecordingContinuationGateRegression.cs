using System;
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
#endif

public static class RecordingContinuationGateRegression
{
    private static int checks;
    private static void Check(bool value, string reason)
    { checks++; if (!value) throw new InvalidOperationException("Continuation gate: " + reason); }

    private static RecordingContinuationGate.Evidence ClosedRequest() => new RecordingContinuationGate.Evidence {
        CurrentCapture = 5, RetainedCapture = 5, FinalCapture = 5,
        HasRetainedAudio = true, SameMicrophone = true,
        FinalSource = "full-preview", FinalText = "能走到我身边来吗？",
        Now = 101.24f, ClosedAt = 100f, ProbeEndedAt = 101.18f, ProbeSeconds = .5f,
        VadAvailable = true
    };

    public static int RunChecks()
    {
        checks = 0;
        var first = ClosedRequest();
        Check(RecordingContinuationGate.ShouldDeferRmsFallback(first),
            "c5->6: completed unheard input plus a fresh negative suffix cannot reopen old PCM by RMS alone");
        foreach (float age in new[] { 1.24f, 1.8f, 2.9f })
        {
            var next = ClosedRequest(); next.Now = 100f + age; next.ProbeEndedAt = next.Now - .06f;
            Check(RecordingContinuationGate.ShouldDeferRmsFallback(next),
                "repeated negative probes keep the same capture owner instead of manufacturing c6/c7/c8");
        }
        // These handoff gaps are observed in the 20260928 snapshot. The actual
        // fallback-triggering /vad timings are not individually logged (the
        // probe trace is throttled). Exercise both sides of the unknown bound;
        // do not label the assumed 50ms RTT a measured production replay.
        foreach (float measuredGap in new[] { 1.2364615f, 1.1578655f, 1.1628197f })
        {
            var value = ClosedRequest(); value.Now = 100f + measuredGap;
            value.ProbeEndedAt = value.Now - .05f;
            Check(RecordingContinuationGate.ShouldDeferRmsFallback(value),
                "observed c6/7/8 gap defers if the 0.5s suffix probe returns within 50ms");
            value.ProbeEndedAt = 100.5f - .01f;
            Check(!RecordingContinuationGate.ShouldDeferRmsFallback(value),
                "same observed gap must pass when the unknown probe overlaps the previous capture");
        }
        void Allows(Action<RecordingContinuationGate.Evidence> change, string why)
        { var value = ClosedRequest(); change(value); Check(!RecordingContinuationGate.ShouldDeferRmsFallback(value), why); }
        Allows(v => { v.Speech = true; v.SpeechMs = 480; }, "c15->16: real supplementary speech immediately passes");
        Allows(v => v.Speech = true, "same recognized words spoken again with new VAD are not deduplicated");
        Allows(v => v.SpeechMs = 60, "subthreshold short real voice is not equivalent to a zero-speech probe");
        Allows(v => v.Tonal = true, "lyric tail or humming exemption keeps continuation PCM");
        Allows(v => v.BargeIn = true, "strict playback-overlap barge-in is outside this gate");
        Allows(v => v.Replied = true, "started output ends the unheard handoff");
        Allows(v => v.VadAvailable = false, "network/protocol failure is not evidence of silence");
        Allows(v => v.ProbeEndedAt = v.Now - .751f, "stale negative result cannot suppress current onset");
        Allows(v => v.ProbeEndedAt = v.Now + .01f, "future timestamp is invalid evidence");
        Allows(v => v.ProbeEndedAt = 100.4f, "probe containing pre-close PCM cannot decide the fresh suffix");
        Allows(v => v.ProbeSeconds = 0f, "empty probe cannot suppress");
        Allows(v => v.ProbeSeconds = float.NaN, "nonfinite probe cannot suppress");
        Allows(v => v.Now = float.PositiveInfinity, "nonfinite clock cannot suppress");
        Allows(v => v.ClosedAt = v.Now + .01f, "negative handoff age is invalid");
        Allows(v => { v.Now = 103.001f; v.ProbeEndedAt = 102.95f; }, "beyond 3s remains an independent candidate");
        Allows(v => v.SameMicrophone = false, "new microphone has no inherited PCM authority");
        Allows(v => v.HasRetainedAudio = false, "independent repeated request remains accepted");
        Allows(v => v.CurrentCapture = 6, "new capture serial cannot be silenced by an old owner");
        Allows(v => v.FinalCapture = 4, "unrelated finalized text cannot justify suppression");
        Allows(v => v.RetainedCapture = 4, "retained PCM and current capture must share identity");
        Allows(v => v.CurrentCapture = v.RetainedCapture = v.FinalCapture = 0, "missing capture identity fails open");
        Allows(v => v.FinalText = "", "unfinished/empty transcript is not an already delivered input");
        foreach (string source in new[] { "pending", "rejected", "evidence-only", "none" })
            Allows(v => v.FinalSource = source, "nonfinal source cannot suppress onset: " + source);
        Allows(v => v.RejectedSelfSpeech = true, "terminal echo stays on the existing discard/isolation path");
        Check(!RecordingContinuationGate.ShouldDeferRmsFallback(null), "missing evidence fails open");
#if UNITY_EDITOR
        RuntimeChecks();
#endif
        return checks;
    }

#if UNITY_EDITOR
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static object Field(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);

    private static void RuntimeChecks()
    {
        var shared = Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
        var host = new GameObject("Inactive continuation gate regression"); host.SetActive(false);
        AudioClip mic = AudioClip.Create("controlled ring", 16000, 1, 16000, false);
        try
        {
            var chat = (ChatSample)Field(shared, "Chat");
            Call(shared, "Arm", "能走到我身边来吗？", true);
            var pending = (FastLoopIntegrationModel.PendingRequest)Call(shared, "Dispatch", new object[] { null });
            int generation = (int)Field(chat, "m_FormalResponseGeneration");
            int inputRevision = (int)Field(chat, "m_InputAudioRevision");
            var provider = host.AddComponent<SenseVoiceSpeechToText>();
            provider.BeginLiveRecordingCandidateSession(); int capture = provider.InputCaptureSessionSerial;
            provider.PublishReusedFinalTranscript(capture, "能走到我身边来吗？");
            var handler = host.AddComponent<RTSpeechHandler>();
            Set(handler, "m_NeuralVadClient", provider); Set(handler, "m_ChatSample", chat);
            Set(handler, "m_RecordedClip", mic); Set(handler, "m_UnheardMic", mic);
            Set(handler, "m_UnheardCaptureSessionSerial", capture);
            Set(handler, "m_UnheardVoiceRevision", chat.VoiceOutputRevision);
            Set(handler, "m_UnheardCaptureClosedAt", Time.realtimeSinceStartup - 1.24f);
            Set(handler, "m_PendingSpeechStartPos", 117); Set(handler, "m_NeuralVadCandidateSince", 42f);
            Set(handler, "m_StreamLastSentPos", 321);
            var pcm = (List<float[]>)Field(handler, "m_UnheardCapture");
            var samples = new float[] { .1f, .2f }; pcm.Add(samples);
            var negative = new SenseVoiceSpeechToText.VoiceActivityResult { Available = true };
            for (int i = 0; i < 3; i++)
                Check((bool)Call(handler, "TryDeferRmsContinuation", negative, Time.realtimeSinceStartup - .05f, .5f, false),
                    "production recorder defers repeated RMS fallback before BeginLiveRecordingCandidateSession");
            Check(provider.InputCaptureSessionSerial == capture && pcm.Count == 1 && ReferenceEquals(pcm[0], samples) &&
                (int)Field(handler, "m_PendingSpeechStartPos") == 117 &&
                (float)Field(handler, "m_NeuralVadCandidateSince") == 42f &&
                (int)Field(handler, "m_StreamLastSentPos") == 321,
                "defer preserves exact PCM, onset, candidate age and cumulative stream for later real speech");
            Check((int)Field(chat, "m_FormalResponseGeneration") == generation &&
                (int)Field(chat, "m_InputAudioRevision") == inputRevision &&
                (bool)Field(chat, "m_FormalResponseInFlight"),
                "negative continuation does not cancel the real pending inference");
            negative.IsSpeech = true; negative.SpeechMs = 480;
            Check(!(bool)Call(handler, "TryDeferRmsContinuation", negative, Time.realtimeSinceStartup - .05f, .5f, false) &&
                (int)Field(handler, "m_PendingSpeechStartPos") == 117,
                "later positive speech falls through with the original pre-roll onset");
            negative.IsSpeech = false; negative.SpeechMs = 0; negative.IsSinging = true;
            Check(!(bool)Call(handler, "TryDeferRmsContinuation", negative, Time.realtimeSinceStartup - .05f, .5f, false),
                "production lyric-tail exemption bypasses defer");
            negative.IsSinging = false;
            Check(!(bool)Call(handler, "TryDeferRmsContinuation", negative, Time.realtimeSinceStartup - .05f, .5f, true),
                "production barge-in bypasses defer");
            Call(provider, "ObserveCaptureIsolation", capture, true, "self-speech", 1f);
            Check(!(bool)Call(handler, "TryDeferRmsContinuation", negative, Time.realtimeSinceStartup - .05f, .5f, false),
                "rejected self-speech is not kept alive by RMS defer");
            pending.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "仍然有效。" }, "ok");
            Check(((string)Call(shared, "Queued")).Contains("仍然有效"), "original request can still publish its answer after three negative candidates");
            var parse = typeof(SenseVoiceSpeechToText).GetMethod("ParseVoiceActivityResult", Flags);
            SenseVoiceSpeechToText.VoiceActivityResult Parse(string wire) =>
                (SenseVoiceSpeechToText.VoiceActivityResult)parse.Invoke(null, new object[] { wire });
            Check(Parse("{\"is_speech\":false,\"speech_ms\":0,\"is_singing\":false}").Available,
                "explicit complete negative server result is usable evidence");
            foreach (string wire in new[] { "", "bad-json", "{}", "{\"error\":\"unavailable\"}",
                "{\"is_speech\":false,\"speech_ms\":0}",
                "{\"is_speech\":false,\"speech_ms\":-1,\"is_singing\":false}" })
                Check(!Parse(wire).Available, "unavailable/malformed/missing VAD is never a negative proof: " + wire);
            Check(Parse("{\"is_speech\":true,\"speech_ms\":400}").IsSpeech,
                "older positive speech protocol stays usable while incomplete negatives cannot suppress");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(mic);
            ((IDisposable)shared).Dispose();
        }
    }
#endif
}
