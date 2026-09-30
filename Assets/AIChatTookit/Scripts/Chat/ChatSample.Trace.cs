using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatSample
{
    private ConversationTrace m_CaptureTrace;
    private int m_TraceCaptureSerial;
    private float m_TracePartialAt = -1f;
    private int m_TracePartialSamples;
    private readonly Dictionary<int, ConversationTrace> m_SingingTraces = new Dictionary<int, ConversationTrace>();
    // A singing request can be accepted while the spoken response is still draining,
    // then be cancelled before a HumBack generation exists. Keep the parent span alive
    // across that queued-only phase so the cancellation is observable as well.
    private ConversationTrace m_QueuedSingingTrace;
    private int m_SingingQueueSerial;
    private int m_ActiveSingingQueueSerial = -1;

    private ConversationTrace SingingInputTraceParent() => AcceptedRecordingInputCurrent
        ? m_AcceptedRecordingInputTrace ?? m_ResponseTrace
        : m_PendingInputTrace ?? m_ResponseTrace;

    private ConversationTrace EnsureQueuedSingingTrace()
    {
        if (m_QueuedSingingTrace != null) return m_QueuedSingingTrace;
        // EOU fast paths run before StartStreaming has installed m_ResponseTrace;
        // prefer the current input span whenever it is available.
        var parent = SingingInputTraceParent();
        if (parent == null) return null;
        m_QueuedSingingTrace = parent.Child("singing-execution");
        return m_QueuedSingingTrace;
    }

    private ConversationTrace SingingTrace(int generation)
    {
        if (m_SingingTraces.TryGetValue(generation, out var trace)) return trace;
        trace = m_QueuedSingingTrace ?? SingingInputTraceParent()?.Child("singing-execution");
        if (trace == null) return null;
        if (m_SingingTraces.Count >= 16) m_SingingTraces.Clear();
        m_SingingTraces[generation] = trace;
        trace.Mark("singing.execution_observed", new JObject { ["generation"] = generation });
        return trace;
    }

    private void TraceSingingQueued(string mode, bool practice, bool catalog,
        float sourceSeconds, float timelineSeconds, int segmentCount, string origin)
    {
        ConversationTrace trace = EnsureQueuedSingingTrace();
        int serial = ++m_SingingQueueSerial;
        m_ActiveSingingQueueSerial = serial;
        trace?.Mark("singing.queued", new JObject {
            ["queueSerial"] = serial,
            ["mode"] = mode ?? "",
            ["practice"] = practice,
            ["catalog"] = catalog,
            ["sourceSeconds"] = sourceSeconds >= 0f ? (double?)sourceSeconds : null,
            ["timelineSeconds"] = timelineSeconds >= 0f ? (double?)timelineSeconds : null,
            ["segmentCount"] = Math.Max(0, segmentCount),
            ["origin"] = origin ?? "accepted",
            ["phase"] = "queued",
            ["scope"] = "accepted-but-not-started"
        });
    }

    private void TraceSingingExecutionBegin(int generation, string rendererCandidate,
        bool practice, bool catalog, bool streamed, bool waitForTextDrain,
        int segmentCount, int sourceBytes, float sourceSeconds, float timelineSeconds)
    {
        ConversationTrace trace = SingingTrace(generation);
        trace?.Mark("singing.execution_begin", new JObject {
            ["generation"] = generation,
            ["queueSerial"] = m_ActiveSingingQueueSerial >= 0 ? (int?)m_ActiveSingingQueueSerial : null,
            ["rendererCandidate"] = rendererCandidate ?? "unknown",
            ["practice"] = practice,
            ["catalog"] = catalog,
            ["streamed"] = streamed,
            ["waitForTextDrain"] = waitForTextDrain,
            ["segmentCount"] = Math.Max(0, segmentCount),
            ["sourceBytes"] = Math.Max(0, sourceBytes),
            ["sourceSeconds"] = sourceSeconds >= 0f ? (double?)sourceSeconds : null,
            ["timelineSeconds"] = timelineSeconds >= 0f ? (double?)timelineSeconds : null,
            ["phase"] = "preparing"
        });
    }

    private void TraceSingingSourcePlay(int generation, string renderer, string playMode,
        int segmentNumber, float clipSeconds, bool scheduled, double scheduledStart = double.NaN)
    {
        ConversationTrace trace = SingingTrace(generation);
        // Use Sample because streamed and fast-prefix playback can legitimately call
        // the source more than once in one singing transaction.
        trace?.Sample("singing.source_play_called", new JObject {
            ["generation"] = generation,
            ["renderer"] = renderer ?? "unknown",
            ["playMode"] = playMode ?? "Play",
            ["segmentNumber"] = segmentNumber > 0 ? (int?)segmentNumber : null,
            ["clipSeconds"] = clipSeconds >= 0f ? (double?)clipSeconds : null,
            ["scheduled"] = scheduled,
            ["scheduledStartDsp"] = !double.IsNaN(scheduledStart) && !double.IsInfinity(scheduledStart)
                ? (double?)scheduledStart : null,
            ["scope"] = "AudioSource-call-not-proof-of-physical-audibility"
        });
    }

    private void TraceSingingCancelled(int generation, string reason, string phase,
        bool hadWork, string activeRequest, int playedSegments, int totalSegments)
    {
        ConversationTrace trace = null;
        if (m_SingingTraces.TryGetValue(generation, out var existing)) trace = existing;
        trace = trace ?? m_QueuedSingingTrace;
        trace?.Mark("singing.cancelled", new JObject {
            ["generation"] = generation >= 0 ? (int?)generation : null,
            ["queueSerial"] = m_ActiveSingingQueueSerial >= 0 ? (int?)m_ActiveSingingQueueSerial : null,
            ["reason"] = reason ?? "unknown",
            ["phase"] = phase ?? "unknown",
            ["hadWork"] = hadWork,
            ["activeRequest"] = activeRequest ?? "none",
            ["playedSegments"] = Math.Max(0, playedSegments),
            ["totalSegments"] = Math.Max(0, totalSegments),
            ["scope"] = "cancelled-before-completion"
        });
        m_QueuedSingingTrace = null;
        m_ActiveSingingQueueSerial = -1;
    }

    private void TraceSingingFinished(int generation, bool completed, string detail,
        string renderer, float playedSeconds, int playedSegments, int totalSegments)
    {
        ConversationTrace trace = SingingTrace(generation);
        trace?.Mark("singing.finished", new JObject {
            ["generation"] = generation,
            ["completed"] = completed,
            ["renderer"] = renderer ?? "unknown",
            ["playedSeconds"] = playedSeconds >= 0f ? (double?)playedSeconds : null,
            ["playedSegments"] = Math.Max(0, playedSegments),
            ["totalSegments"] = Math.Max(0, totalSegments),
            ["detailAvailable"] = !string.IsNullOrWhiteSpace(detail),
            ["outcome"] = completed ? "played" : "failed_or_interrupted",
            ["scope"] = "existing-runtime-completion-not-proof-of-physical-audibility"
        });
        if (!completed)
            trace?.Mark("singing.failed", new JObject {
                ["generation"] = generation,
                ["renderer"] = renderer ?? "unknown",
                ["detailAvailable"] = !string.IsNullOrWhiteSpace(detail),
                ["playedSeconds"] = playedSeconds >= 0f ? (double?)playedSeconds : null,
                ["playedSegments"] = Math.Max(0, playedSegments),
                ["totalSegments"] = Math.Max(0, totalSegments)
            });
        m_QueuedSingingTrace = null;
        m_ActiveSingingQueueSerial = -1;
    }

    public void TraceCaptureStarted(int captureSerial, bool resumed, bool tonalOverride)
    {
        m_TraceCaptureSerial = captureSerial;
        m_CaptureTrace = NewConversationTrace("voice-capture");
        m_TracePartialAt = -1f; m_TracePartialSamples = 0;
        m_CaptureTrace?.Mark("capture.started", new JObject { ["captureSerial"] = captureSerial,
            ["resumed"] = resumed, ["tonalOverride"] = tonalOverride,
            ["scope"] = "accepted-recording-start-including-preroll-not-physical-voice-onset" });
        (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.RegisterCaptureTrace(captureSerial, m_CaptureTrace);
    }

    public void TracePartialAsr(int captureSerial, SenseVoiceSpeechToText.StreamingTranscript part)
    {
        if (m_CaptureTrace == null || captureSerial != m_TraceCaptureSerial || part == null) return;
        m_CaptureTrace.Mark("asr.partial_first");
        // First + at most one summary per second, bounded per recording. No transcript content.
        if (m_TracePartialSamples >= 300 || (m_TracePartialAt >= 0f && Time.realtimeSinceStartup - m_TracePartialAt < 1f)) return;
        m_TracePartialAt = Time.realtimeSinceStartup; m_TracePartialSamples++;
        m_CaptureTrace.Sample("asr.partial_sample", new JObject {
            ["captureSerial"] = captureSerial, ["audioMs"] = part.AudioMs, ["windowStartMs"] = part.WindowStartMs,
            ["characters"] = (part.Text ?? "").Length, ["stableCharacters"] = (part.StableText ?? "").Length,
            ["revision"] = part.Revision, ["singingProbability"] = part.SingingProbability,
            ["pitchStability"] = part.PitchStability, ["activityAvailable"] = part.ActivityAvailable,
            ["vadAvailable"] = part.ActivityVadAvailable, ["speechMs"] = part.ActivitySpeechMs,
            ["speechEndAgeMs"] = part.ActivitySpeechEndAgeMs, ["periodicity"] = part.ActivityPeriodicity,
            ["voicedRatio"] = part.ActivityVoicedRatio, ["rms"] = part.ActivityRms });
    }

    public void TraceCaptureStreamChanged(int captureSerial, string state, string reason,
        int generation, float submittedSeconds, float captureAgeSeconds)
    {
        if (m_CaptureTrace == null || captureSerial != m_TraceCaptureSerial) return;
        string normalized = (state ?? "").Trim().ToLowerInvariant();
        string eventName = normalized == "error"
            ? "capture.stream_failed"
            : normalized == "closed"
                ? "capture.stream_closed"
                : normalized == "open"
                    ? "capture.stream_open"
                    : "capture.stream_state";
        m_CaptureTrace.Mark(eventName, new JObject {
            ["captureSerial"] = captureSerial,
            ["generation"] = generation,
            ["state"] = normalized,
            ["reason"] = reason ?? "",
            ["submittedSeconds"] = submittedSeconds >= 0f ? (double?)submittedSeconds : null,
            ["captureAgeSeconds"] = captureAgeSeconds >= 0f ? (double?)captureAgeSeconds : null
        });
    }

    public void TraceCaptureStopRequested(int captureSerial, string reason,
        float captureAgeSeconds, float submittedSeconds, int capturedFrames, bool micRecording)
    {
        if (m_CaptureTrace == null || captureSerial != m_TraceCaptureSerial) return;
        m_CaptureTrace.Mark("capture.stop_requested", new JObject {
            ["captureSerial"] = captureSerial,
            ["reason"] = reason ?? "unknown",
            ["captureAgeSeconds"] = captureAgeSeconds >= 0f ? (double?)captureAgeSeconds : null,
            ["submittedSeconds"] = submittedSeconds >= 0f ? (double?)submittedSeconds : null,
            ["capturedFrames"] = Math.Max(0, capturedFrames),
            ["micRecording"] = micRecording
        });
    }

    public void TraceCaptureWatchdog(int captureSerial, string reason, float triggerAge,
        string streamState, string streamReason, float submittedSeconds)
    {
        if (m_CaptureTrace == null || captureSerial != m_TraceCaptureSerial) return;
        m_CaptureTrace.Mark("capture.watchdog_timeout", new JObject {
            ["captureSerial"] = captureSerial,
            ["reason"] = reason ?? "unknown",
            ["triggerAgeSeconds"] = triggerAge >= 0f ? (double?)triggerAge : null,
            ["streamState"] = streamState ?? "",
            ["streamReason"] = streamReason ?? "",
            ["submittedSeconds"] = submittedSeconds >= 0f ? (double?)submittedSeconds : null
        });
    }

    private static JObject TraceHttpResult(UnityWebRequest request, bool current) => new JObject {
        ["current"] = current, ["httpStatus"] = request.responseCode,
        ["transportResult"] = request.result.ToString(), ["error"] = request.error };
}
