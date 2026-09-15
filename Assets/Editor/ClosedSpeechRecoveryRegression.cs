using System;
#if UNITY_EDITOR
using System.Reflection;
using UnityEngine;
#endif

public static class ClosedSpeechRecoveryRegression
{
    public static int RunChecks()
    {
        int checks = 0;
        Action<bool, string> check = (condition, name) => {
            checks++;
            if (!condition) throw new InvalidOperationException("Closed speech recovery: " + name);
        };
        Func<string, ClosedSpeechEvidenceTracker> ready = text => {
            var tracker = new ClosedSpeechEvidenceTracker();
            tracker.Begin(7, 0f);
            tracker.Observe(7, text, "", 890, .89f, true);
            tracker.Observe(7, text, text, 1450, 1.45f, false);
            return tracker;
        };
        Func<ClosedSpeechEvidenceTracker, ClosedSpeechEvidence> seal = tracker => {
            tracker.TrySeal(7, 1.8f, 1800, true, true, false, true, "llm-take_turn", out var e);
            return e;
        };
        var first = ready("停一下。");
        var evidence = seal(first);
        check(evidence != null && evidence.Text == "停一下。", "stable closed short speech retained");
        check(evidence.CaptureSessionSerial == 7 && evidence.AudioMs == 1450 &&
            evidence.SubmittedAudioMs == 1800 && evidence.StableForSeconds > .8f,
            "capture and coverage provenance retained");
        check(seal(first) == null, "EOU candidate is published once");
        var copy = evidence.Copy(); copy.Text = "other";
        check(evidence.Text == "停一下。", "published copies cannot rewrite retained evidence");
        foreach (string text in new[] { "不要停，继续走。", "他说‘停一下’，不是叫你停。", "止まらないで。", "Do not stop." })
            check(seal(ready(text)).Text == text, "negation and quotation are observations, never commands: " + text);
        foreach (string reason in new[] { "manual-disable", "llm-interrupt_user", "" })
        {
            var tracker = ready("停一下。");
            check(!tracker.TrySeal(7, 1.8f, 1800, true, true, false, true, reason, out _),
                "non-endpoint is rejected: " + reason);
        }
        foreach (int condition in new[] { 0, 1, 2, 3 })
        {
            var tracker = ready("停一下。");
            check(!tracker.TrySeal(7, 1.8f, 1800, condition != 0, condition != 1,
                condition == 2, condition != 3, "llm-take_turn", out _),
                "EOU/coverage/revision/speech gate " + condition);
        }
        var revised = ready("停一下。");
        revised.Observe(7, "不要停。", "不要停。", 1700, 1.7f, true);
        check(seal(revised) == null, "late negation invalidates the earlier stable candidate");
        revised.Observe(7, "不要停。", "不要停。", 2000, 2f, false);
        check(revised.TrySeal(7, 2.2f, 2250, true, true, false, true, "effective-quiet", out var denied) &&
            denied.Text == "不要停。", "stable revised phrase reaches semantic layer unchanged");
        var emptied = ready("停一下。"); emptied.Observe(7, "", "", 1700, 1.7f, false);
        check(seal(emptied) == null, "later empty packet does not reuse the last nonempty text");
        var old = ready("停一下。"); old.Begin(8, 2f);
        old.Observe(7, "停一下。", "停一下。", 3000, 3f, true);
        check(!old.TrySeal(8, 3.5f, 3500, true, true, false, true, "effective-quiet", out _),
            "old recording callback cannot populate the new recording");
        var duplicate = new ClosedSpeechEvidenceTracker(); duplicate.Begin(7, 0f);
        duplicate.Observe(7, "停一下。", "", 890, .89f, true);
        duplicate.Observe(7, "停一下。", "", 890, 1.45f, true);
        check(seal(duplicate) == null, "duplicate audio timestamp is not stable progress");
        var noVoice = new ClosedSpeechEvidenceTracker(); noVoice.Begin(7, 0f);
        noVoice.Observe(7, "停一下。", "停一下。", 890, .89f, false);
        noVoice.Observe(7, "停一下。", "停一下。", 1450, 1.45f, false);
        check(seal(noVoice) == null, "text alone without acoustic speech evidence is insufficient");
        check(!ready("停一下。").TrySeal(7, 1.8f, 3000, true, true, false, true, "effective-quiet", out _),
            "uncovered last audio is rejected");
        check(!ready("停一下。").TrySeal(7, 4f, 1800, true, true, false, true, "effective-quiet", out _),
            "stalled stream cannot masquerade as stable text");
        check(!ready("停一下。").TrySeal(7, float.NaN, 1800, true, true, false, true, "effective-quiet", out _),
            "nonfinite closure clock rejected");
        check(!ready(new string('好', 121)).TrySeal(7, 1.8f, 1800, true, true, false, true, "effective-quiet", out _),
            "long message does not use short-speech recovery");
        foreach (string reason in new[] { "vad-insufficient-speech", "recognizer-empty" })
            check(ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, reason, "", 2f),
                "explicit recoverable final reason: " + reason);
        foreach (string reason in new[] { "self-speech", "hallucination-rejected", "unknown-no-speech", "http-error",
            "cancelled", "detached", "invalid-audio", "empty_tensor_asr", "empty_upload", "" })
            check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, reason, "", 2f),
                "final rejection is not overwritten: " + reason);
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 8, 7, true, "recognizer-empty", "", 2f), "new capture veto");
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 8, true, "recognizer-empty", "", 2f), "wrong request capture veto");
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, false, "recognizer-empty", "", 2f), "preview cannot recover");
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "recognizer-empty", "不要停。", 2f),
            "nonempty final always wins even if it reverses the candidate");
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "recognizer-empty", "", 17f), "expired evidence veto");
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "recognizer-empty", "", 1f), "before closure veto");
        check(ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "hallucination-rejected", "", 2f,
            "停一下。", true, 480), "recorded short-phrase gate conflict requires complete decoder agreement plus raw VAD");
        foreach (string changed in new[] { "不要停。", "停", "继续走。", "", "I." })
            check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "hallucination-rejected", "", 2f,
                changed, true, 480), "rejected decoder disagreement is not overwritten: " + changed);
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "hallucination-rejected", "", 2f,
            "停一下。", false, 480), "tonal VAD override cannot establish direct speech");
        check(!ClosedSpeechEvidenceTracker.MayRecover(evidence, 7, 7, true, "hallucination-rejected", "", 2f,
            "停一下。", true, 100), "insufficient direct VAD cannot bypass noise gate");
        var noStop = seal(ready("不要停。"));
        check(ClosedSpeechEvidenceTracker.MayRecover(noStop, 7, 7, true, "hallucination-rejected", "", 2f,
            "不要停。", true, 480), "same agreement recovers negation as spoken; semantic planner must choose no stop");
#if UNITY_EDITOR
        var host = new GameObject("Closed speech recovery regression");
        try
        {
            var provider = host.AddComponent<SenseVoiceSpeechToText>();
            provider.BeginLiveRecordingCandidateSession();
            int capture = provider.InputCaptureSessionSerial;
            var bound = evidence.Copy(); bound.CaptureSessionSerial = capture; bound.ClosedAt = Time.realtimeSinceStartup;
            provider.RetainClosedSpeechEvidence(bound);
            var recover = typeof(SenseVoiceSpeechToText).GetMethod("TryRecoverClosedTranscript", BindingFlags.Instance | BindingFlags.NonPublic);
            object[] args = { capture, true, "vad-insufficient-speech", "", null, "", false, 0 };
            check((bool)recover.Invoke(provider, args) && (string)args[4] == evidence.Text,
                "provider restores exact same-capture candidate");
            check(!(bool)recover.Invoke(provider, args), "provider recovery is single-use");
            provider.PublishReusedFinalTranscript(capture, "不要停。");
            check(provider.LastFinalCaptureSerial == capture && provider.LastFinalRawText == "不要停。" &&
                provider.LastFinalTranscriptSource == "full-preview", "full preview publishes exact final metadata");
            provider.BeginLiveRecordingCandidateSession();
            provider.PublishReusedFinalTranscript(capture, "过期停下。");
            check(provider.LastFinalCaptureSerial != capture && provider.LastFinalTranscriptSource == "pending" &&
                provider.LastFinalRawText == "", "old preview cannot publish into newer capture");
            provider.RetainClosedSpeechEvidence(bound);
            check(!(bool)recover.Invoke(provider, args), "old retained candidate cannot recover after capture begins");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
#endif
        return checks;
    }
}
