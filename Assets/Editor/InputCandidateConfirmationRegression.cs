using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using UnityEngine;

// Edit-mode production confirmation and real pending-reply cancellation gates.
// Controlled acoustic packets; no microphone, playback, network or model calls.
public static class InputCandidateConfirmationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Run in edit mode without microphone startup.");
        checks = 0;
        using (var f = new Fixture())
        {
            Set(f.Handler, "m_MelodicEouProtectionActive", true);
            Set(f.Handler, "m_MelodicEouEvidenceFrames", 3);
            var weak = Packet(1000); weak.IsSinging = true;
            weak.SingingProbability = .49f; weak.PitchStability = .50f;
            weak.ActivityVoicedRatio = .398f;
            Call(f.Handler, "UpdateMelodicEouProtection", weak);
            Check((int)Get(f.Handler, "m_MelodicEouEvidenceFrames") == 4,
                "Fixture reproduces the old preloaded EOU counter accepting one weak IsSinging frame.");
            f.Observe(weak);
            f.Unchanged("Preloaded EOU count plus weak first frame cannot cancel the prepared answer.");
            Check(f.MelodyFrames == 0, "The independent strong-current counter starts at zero.");
            f.AnswerOld();
            Check(f.Queued.Contains("仍然有效"), "The original pending reply remains publishable after weak evidence.");
        }
        using (var f = new Fixture())
        {
            f.Observe(Packet(1000));
            f.Observe(Packet(1000));
            f.Observe(Packet(800));
            f.Observe(Packet(1100));
            f.Unchanged("Duplicate, older and nearly identical audio windows cannot manufacture sustained evidence.");
            Check(f.MelodyFrames == 1, "Only the first independently advancing strong window counted.");
            f.Observe(Packet(1300));
            f.Unchanged("Two current strong observations remain provisional.");
            f.Observe(Packet(1600));
            f.Confirmed("Three current above-noise voiced/periodic observations confirm real sustained input.");
            int revision = (int)Get(f.Chat, "m_InputAudioRevision");
            f.Observe(Packet(1900));
            Check((int)Get(f.Chat, "m_InputAudioRevision") == revision, "An already confirmed capture interrupts at most once.");
            f.AnswerOld();
            Check(!f.Queued.Contains("仍然有效"), "A stale callback from the cancelled old reply cannot enter the speech queue.");
        }
        using (var f = new Fixture())
        {
            f.Observe(Packet(1000)); f.Observe(Packet(1500));
            f.Observe(Packet(2000), true);
            f.Observe(Speech(2500), true);
            f.Unchanged("Actual playback/echo protection denies both melody and speech evidence on this path.");
            Check(f.MelodyFrames == 0, "Own-output observations clear earlier provisional melody authority.");
            f.Observe(Packet(2500));
            Check(f.MelodyFrames == 0, "A protected packet cannot be replayed after protection ends.");
            f.Observe(Packet(3000)); f.Observe(Packet(3500));
            f.Unchanged("New evidence must rebuild its own uninterrupted history after playback.");
            f.Observe(Packet(4000));
            f.Confirmed("Three new post-protection observations can confirm.");
        }
        using (var f = new Fixture())
        {
            f.Observe(Speech(1000));
            f.Confirmed("Recent independent speech VAD can confirm immediately without waiting for melody frames.");
        }
        using (var f = new Fixture())
        {
            f.Observe(Packet(1000), false, 8f);
            f.Observe(Speech(1500), false, 8f);
            f.Unchanged("Late packets of old sound cannot confirm current input.");
            var missing = Packet(2000); missing.ActivityAvailable = false;
            f.Observe(missing);
            var quiet = Packet(2500); quiet.ActivityRms = .0001f;
            f.Observe(quiet);
            f.Unchanged("Missing current-tail data or below-noise sound remains evidence only.");
            Check(f.MelodyFrames == 0, "Unsupported observations do not accumulate.");
        }
        using (var f = new Fixture())
        {
            f.Observe(Packet(1000)); f.Observe(Packet(1500));
            Call(f.Handler, "ResetRecordingInputConfirmation");
            f.Observe(Packet(500));
            Check(f.MelodyFrames == 1, "A new capture starts at zero, allowing its own earlier time base.");
            f.Unchanged("Two observations from an old capture cannot confirm a new one.");
            var weak = Packet(1000); weak.ActivityPeriodicity = .5f;
            f.Observe(weak); f.Observe(Packet(1500));
            Check(f.MelodyFrames == 1, "A weak intervening current window breaks the sustained sequence.");
            f.Observe(Packet(5000));
            Check(f.MelodyFrames == 1, "A long gap also breaks continuous melody evidence.");
        }
        using (var f = new Fixture())
        {
            Set(f.Handler, "m_IsRecording", false);
            f.Observe(Speech(1000));
            f.Unchanged("A closed capture cannot confirm on a late callback.");
        }
        using (var f = new Fixture())
        {
            int serial = f.Handler.CurrentCaptureSerial;
            Set(f.Handler, "m_CurrentRecordingAllowsSpeakerLearning", true);
            Call(f.Handler, "MarkRecordingPlaybackOverlap");
            Check(!(bool)Get(f.Handler, "m_CurrentRecordingAllowsSpeakerLearning") &&
                (bool)Get(f.Handler, "m_RecordingOutputOverlap"),
                "Playback overlap disables voiceprint learning without discarding the candidate.");
            f.Unchanged("Recording overlap alone cannot grant barge-in permission.");
            Check((bool)Call(f.Handler, "IsVadProbeCaptureCurrent", serial, true),
                "A strict probe can retain its existing capture owner.");
            Check(!(bool)Call(f.Handler, "IsVadProbeCaptureCurrent", serial + 1, true) &&
                !(bool)Call(f.Handler, "IsVadProbeCaptureCurrent", serial, false),
                "A new capture or changed recording state rejects the old probe callback.");
            Set(f.Handler, "m_RecordingStartPos", 117);
            Set(f.Handler, "m_RecordingCapturedFrames", 4800);
            Set(f.Handler, "m_RecordingRescuedByTonalOverride", true);
            // The real Interrupt path requires the scene's TTS binding even
            // when no physical audio is played in this controlled fixture.
            f.AttachSpeechProvider();
            Set(f.Chat, "<IsAISpeaking>k__BackingField", true);
            Call(f.Handler, "TriggerBargeIn", 900);
            f.Confirmed("A confirmed strict AEC/identity result can promote the same provisional capture.");
            Check((bool)Get(f.Handler, "m_IsRecording") && f.Handler.CurrentCaptureSerial == serial &&
                (int)Get(f.Handler, "m_RecordingStartPos") == 117 &&
                (int)Get(f.Handler, "m_RecordingCapturedFrames") == 4800,
                "Promotion keeps the original onset and accumulated PCM rather than reopening the capture.");
            Check(!(bool)Get(f.Handler, "m_RecordingRescuedByTonalOverride") &&
                !(bool)Get(f.Handler, "m_CurrentRecordingAllowsSpeakerLearning"),
                "Confirmed mixed capture leaves tonal-only protection but never enrols playback-mixed voice.");
            f.AnswerOld();
            Check(!f.Queued.Contains("仍然有效"), "Real barge-in promotion fences the old reply callback.");
        }
        Debug.Log("[InputCandidateConfirmationRegression] passed " + checks + " checks");
        return checks;
    }

    private static SenseVoiceSpeechToText.StreamingTranscript Packet(int audioMs) =>
        new SenseVoiceSpeechToText.StreamingTranscript {
            AudioMs = audioMs, Text = "", StableText = "", IsSinging = false,
            SingingProbability = .49f, PitchStability = .5f,
            ActivityAvailable = true, ActivityWindowMs = 800, ActivityVadAvailable = true,
            ActivitySpeechMs = 0, ActivitySpeechEndAgeMs = -1,
            ActivityRms = .03f, ActivityPeriodicity = .85f, ActivityVoicedRatio = .8f
        };

    private static SenseVoiceSpeechToText.StreamingTranscript Speech(int audioMs)
    {
        var value = Packet(audioMs);
        value.ActivitySpeechMs = 400; value.ActivitySpeechEndAgeMs = 20;
        value.ActivityPeriodicity = .1f; value.ActivityVoicedRatio = .1f;
        return value;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object shared;
        private readonly GameObject host;
        private readonly FastLoopIntegrationModel.PendingRequest request;
        private readonly int generation, inputRevision;
        public readonly ChatSample Chat;
        public readonly RTSpeechHandler Handler;
        public int MelodyFrames => (int)Get(Handler, "m_ConfirmationMelodyFrames");
        public string Queued => (string)Call(shared, "Queued");

        public Fixture()
        {
            shared = Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
            Chat = (ChatSample)Get(shared, "Chat");
            Call(shared, "Arm", "正在等待的原问题。", true);
            request = (FastLoopIntegrationModel.PendingRequest)Call(shared, "Dispatch", new object[] { null });
            generation = (int)Get(Chat, "m_FormalResponseGeneration");
            inputRevision = (int)Get(Chat, "m_InputAudioRevision");
            host = new GameObject("Inactive input confirmation regression") { hideFlags = HideFlags.HideAndDontSave };
            host.SetActive(false); // Do not initialize microphone/events/Start.
            // The real cancellation path clears the scene's response Text.
            // Shared FastLoop fixtures do not normally call this UI boundary.
            var subtitle = new GameObject("Controlled response text", typeof(RectTransform));
            subtitle.transform.SetParent(host.transform, false);
            Set(Chat, "m_TextBack", subtitle.AddComponent<UnityEngine.UI.Text>());
            Handler = host.AddComponent<RTSpeechHandler>();
            Set(Handler, "m_ChatSample", Chat); Set(Handler, "m_IsRecording", true);
            Set(Handler, "m_EnableSingingMode", true); Set(Handler, "m_LogTimings", false);
            Set(Handler, "m_AmbientNoiseCalibrated", true);
            Set(Handler, "m_AmbientRmsFloor", .001f); Set(Handler, "m_AmbientRmsUpper", .001f);
            var activity = (TurnActivityEvidence)Get(Handler, "m_TurnActivity");
            float now = Time.realtimeSinceStartup;
            activity.Reset(now);
            activity.ObserveLevel(.03f, now, .001f, .001f);
            Call(Handler, "ResetRecordingInputConfirmation");
        }

        public void Observe(SenseVoiceSpeechToText.StreamingTranscript packet, bool outputProtected = false, float submittedSeconds = -1f)
        {
            Set(Handler, "m_StreamSubmittedSeconds", submittedSeconds < 0f ? packet.AudioMs / 1000f : submittedSeconds);
            Set(Handler, "m_LastStreamAudioSubmittedAt", Time.realtimeSinceStartup);
            Call(Handler, "ObserveRecordingInputConfirmation", packet, outputProtected);
        }

        public void Unchanged(string reason)
        {
            Check(!(bool)Get(Handler, "m_RecordingInputConfirmed") &&
                  (int)Get(Chat, "m_FormalResponseGeneration") == generation &&
                  (int)Get(Chat, "m_InputAudioRevision") == inputRevision &&
                  (bool)Get(Chat, "m_FormalResponseInFlight"), reason);
        }

        public void Confirmed(string reason)
        {
            Check((bool)Get(Handler, "m_RecordingInputConfirmed") &&
                  (int)Get(Chat, "m_InputAudioRevision") == inputRevision + 1 &&
                  !(bool)Get(Chat, "m_FormalResponseInFlight"), reason);
        }

        public void AttachSpeechProvider() => Call(shared, "AttachSpeechProvider");

        public void AnswerOld() => request.Answer(new FastLoopDecision {
            Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "原来的回答仍然有效。"
        }, "ok");

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(host);
            ((IDisposable)shared).Dispose();
        }
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        catch (TargetInvocationException e)
        {
            ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw();
            throw;
        }
    }
    private static void Check(bool condition, string reason)
    { checks++; if (!condition) throw new InvalidOperationException(reason); }
}
