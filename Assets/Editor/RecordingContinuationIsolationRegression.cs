using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
#endif

public static class RecordingContinuationIsolationRegression
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException("Continuation isolation: " + message);
    }

    public static int RunChecks()
    {
        checks = 0;
        var guard = new RecordingContinuationIsolation();
        // Captures 39 -> 40: terminal self-speech rejected before a continuation.
        Check(guard.Reject(39, true, "self-speech", 10.57f), "sealed capture 39 is rejected");
        guard.Bind(40, 39, 10.57f);
        Check(Near(guard.InheritedRejectedSeconds(40), 10.57f), "new serial cannot restore the rejected 10.57 seconds");
        Check(!guard.IsRejected(40), "new overlapping user's whole capture is not marked self-speech");
        Check(guard.InheritedRejectedSeconds(41) == 0f, "independent new user capture remains usable");
        Check(!guard.Reject(39, true, "self-speech", 9f) && Near(guard.InheritedRejectedSeconds(40), 10.57f),
            "a shorter duplicate cannot shrink a rejection boundary");

        var normal = new RecordingContinuationIsolation();
        normal.Bind(2, 1, 5f); normal.Bind(3, 2, 9f);
        Check(normal.InheritedRejectedSeconds(3) == 0f, "normal continuation and held lyric tail retain all PCM");
        foreach (string reason in new[] { "", "vad-insufficient-speech", "recognizer-empty", "http-error", "evidence-only" })
            Check(!normal.Reject(1, true, reason, 5f), "non-speaker rejection is not an identity veto: " + reason);
        Check(!normal.Reject(1, false, "self-speech", 2f), "unfinished preview cannot discard later real user speech");
        Check(normal.InheritedRejectedSeconds(3) == 0f, "nonterminal evidence leaves normal continuation intact");
        Check(normal.Reject(1, true, "self-speech", 5f), "late sealed rejection is remembered");
        Check(Near(normal.InheritedRejectedSeconds(3), 5f), "late ancestor rejection removes only its prefix across two joins");
        normal.Reject(2, true, "self-speech", 9f);
        Check(Near(normal.InheritedRejectedSeconds(3), 9f), "later whole-prefix rejection extends the boundary");
        normal.Bind(3, 3, 1f); normal.Bind(1, 3, 1f);
        Check(Near(normal.InheritedRejectedSeconds(3), 9f), "cycles and nonmonotonic capture bindings are ignored");
        normal.Bind(4, 3, float.NaN); normal.Bind(5, 3, float.PositiveInfinity);
        Check(normal.InheritedRejectedSeconds(4) == 0f && normal.InheritedRejectedSeconds(5) == 0f,
            "nonfinite lineage durations rejected");
        Check(!normal.Reject(0, true, "self-speech", 1f), "unknown capture identity cannot reject all future audio");

        Check(RecordingContinuationIsolation.ClampPreRoll(70, 95, 90, 100) == 90, "fresh pre-roll cannot recopy rejected tail");
        Check(RecordingContinuationIsolation.ClampPreRoll(90, 10, 5, 100) == 5, "pre-roll fence handles microphone ring wrap");
        Check(RecordingContinuationIsolation.ClampPreRoll(70, 95, 40, 100) == 70, "a fence before the new pre-roll keeps all fresh speech");
        Check(RecordingContinuationIsolation.ClampPreRoll(70, 95, 95, 100) == 95, "no fresh frames returns the current endpoint");

        foreach (int channels in new[] { 1, 2 })
        {
            byte[] mixed = Wav(1000, channels, 10570, 3000);
            byte[] original = (byte[])mixed.Clone();
            Check(RecordingContinuationIsolation.TryRemovePrefix(mixed, 10.57f, out byte[] suffix, out float cut),
                "crop full self-speech prefix, channels=" + channels);
            Check(Near(cut, 10.57f) && suffix.Length == 44 + 3000 * channels * 2,
                "exact post-rejection duration retained");
            for (int i = 44; i < suffix.Length; i += 2)
                if (BitConverter.ToInt16(suffix, i) != 2345) throw new InvalidOperationException("Rejected echo PCM survived crop");
            Check(Equal(mixed, original), "shared original PCM remains immutable");
        }
        byte[] shortTail = Wav(1000, 1, 1000, 10);
        Check(RecordingContinuationIsolation.TryRemovePrefix(shortTail, 1f, out byte[] shortResult, out _) &&
            shortResult.Length == 64 && BitConverter.ToInt16(shortResult, 44) == 2345,
            "ten-millisecond genuine tail is preserved without returning old prefix");
        Check(RecordingContinuationIsolation.TryRemovePrefix(shortTail, 2f, out byte[] empty, out _) && empty.Length == 0,
            "all rejected PCM yields empty input, never original waveform");
        Check(!RecordingContinuationIsolation.TryRemovePrefix(new byte[100], 1f, out _, out _),
            "malformed waveform fails closed when a crop is required");
#if UNITY_EDITOR
        ProviderAndRecorderChecks();
#endif
        return checks;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    private static bool Equal(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    private static byte[] Wav(int rate, int channels, int echoFrames, int userFrames)
    {
        int bytes = (echoFrames + userFrames) * channels * 2;
        byte[] w = new byte[44 + bytes];
        void Put(int offset, byte[] value) => Buffer.BlockCopy(value, 0, w, offset, value.Length);
        Put(0, System.Text.Encoding.ASCII.GetBytes("RIFF")); Put(4, BitConverter.GetBytes(w.Length - 8));
        Put(8, System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); Put(16, BitConverter.GetBytes(16));
        Put(20, BitConverter.GetBytes((short)1)); Put(22, BitConverter.GetBytes((short)channels));
        Put(24, BitConverter.GetBytes(rate)); Put(28, BitConverter.GetBytes(rate * channels * 2));
        Put(32, BitConverter.GetBytes((short)(channels * 2))); Put(34, BitConverter.GetBytes((short)16));
        Put(36, System.Text.Encoding.ASCII.GetBytes("data")); Put(40, BitConverter.GetBytes(bytes));
        for (int frame = 0; frame < echoFrames + userFrames; frame++)
            for (int channel = 0; channel < channels; channel++)
                Put(44 + (frame * channels + channel) * 2, BitConverter.GetBytes((short)(frame < echoFrames ? -1234 : 2345)));
        return w;
    }

#if UNITY_EDITOR
    private static void ProviderAndRecorderChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = new GameObject("Continuation input isolation regression"); host.SetActive(false);
        try
        {
            var provider = host.AddComponent<SenseVoiceSpeechToText>();
            var handler = host.AddComponent<RTSpeechHandler>();
            Type rt = typeof(RTSpeechHandler), stt = typeof(SenseVoiceSpeechToText);
            void Set(string name, object value) => rt.GetField(name, flags).SetValue(handler, value);
            object Field(string name) => rt.GetField(name, flags).GetValue(handler);
            Set("m_NeuralVadClient", provider);
            provider.BeginLiveRecordingCandidateSession(); int earlier = provider.InputCaptureSessionSerial;
            var retained = (List<float[]>)Field("m_UnheardCapture"); retained.Add(new float[] { -.5f, -.6f });
            Set("m_UnheardCaptureSessionSerial", earlier); Set("m_UnheardEndPos", 90);
            Set("m_UnheardCaptureClosedAt", Time.realtimeSinceStartup); Set("m_StreamLastSentPos", 90);
            rt.GetMethod("DiscardRejectedUnheardCapture", flags).Invoke(handler, null);
            Check(retained.Count == 1 && (int)Field("m_StreamLastSentPos") == 90,
                "normal unheard lyric/user PCM and stream are retained");
            stt.GetMethod("ObserveCaptureIsolation", flags).Invoke(provider, new object[] { earlier, true, "self-speech", 1f });
            rt.GetMethod("DiscardRejectedUnheardCapture", flags).Invoke(handler, null);
            Check(retained.Count == 0 && (int)Field("m_StreamLastSentPos") == -1,
                "terminal self-speech discards both PCM and cumulative partial stream");
            Check((int)Field("m_RejectedContinuationEndPos") == 90,
                "rejected endpoint retained to fence pre-roll, not to disable new capture");
            provider.BeginLiveRecordingCandidateSession(); provider.SetInputCaptureOverlap(earlier, 1f);
            byte[] input = Wav(1000, 1, 1000, 500);
            object[] args = { provider.InputCaptureSessionSerial, input, 0f };
            Check((bool)stt.GetMethod("IsolateContinuationAudio", flags).Invoke(provider, args) &&
                ((byte[])args[1]).Length == 1044 && Near((float)args[2], 1f),
                "production provider sends only genuine suffix after late ancestor rejection");
            object[] again = { provider.InputCaptureSessionSerial, args[1], args[2] };
            Check((bool)stt.GetMethod("IsolateContinuationAudio", flags).Invoke(provider, again) &&
                ReferenceEquals(again[1], args[1]), "reanalyzed suffix is not cropped twice");
            provider.BeginLiveRecordingCandidateSession();
            object[] fresh = { provider.InputCaptureSessionSerial, input, 0f };
            Check((bool)stt.GetMethod("IsolateContinuationAudio", flags).Invoke(provider, fresh) &&
                ReferenceEquals(fresh[1], input), "fresh user including playback overlap is not globally blocked");
            RetainedPreviewChecks(provider);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RetainedPreviewChecks(SenseVoiceSpeechToText provider)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type stt = typeof(SenseVoiceSpeechToText), evidenceType = stt.GetNestedType("SingingEvidenceSnapshot", flags);
        void Set(object target, string name, object value) { var field = target.GetType().GetField(name, flags); if (field != null) field.SetValue(target, value); else target.GetType().GetProperty(name, flags).SetValue(target, value); }
        object Get(object target, string name) { var field = target.GetType().GetField(name, flags); return field != null ? field.GetValue(target) : target.GetType().GetProperty(name, flags).GetValue(target); }
        object Call(string name, params object[] args) => stt.GetMethod(name, flags).Invoke(provider, args);
        object Evidence(int capture, byte[] wav, float seconds, string words)
        {
            var value = Activator.CreateInstance(evidenceType, true);
            Set(value, "CaptureSessionSerial", capture); Set(value, "RawWavBytes", wav);
            Set(value, "RawSeconds", seconds); Set(value, "Text", words); Set(value, "SingingText", words);
            Set(value, "Language", "ja"); Set(value, "PitchTimelineMidi", Enumerable.Repeat(60f, (int)(seconds * 10)).ToArray());
            Set(value, "FrameSeconds", .1f); Set(value, "CleanEndSeconds", seconds);
            Set(value, "RecoveryEndSeconds", seconds); return value;
        }
        var candidates = (IList)Get(provider, "m_QuarantinedSingingCandidates");
        var practices = (IList)Get(provider, "m_PracticePhrases");
        provider.BeginLiveRecordingCandidateSession(); int other = provider.InputCaptureSessionSerial;
        byte[] original = Wav(1000, 1, 0, 4000);
        var otherEvidence = Evidence(other, original, 4f, "unrelated song");
        Set(provider, "m_LastSingingEvidence", otherEvidence);
        Check(provider.RetainCurrentSingingEvidenceClip(true), "unrelated recording retained by actual production method");
        var otherPhrase = Get(candidates[candidates.Count - 1], "Phrase");
        candidates.RemoveAt(candidates.Count - 1); practices.Add(otherPhrase);
        provider.BeginLiveRecordingCandidateSession(); int parent = provider.InputCaptureSessionSerial;
        provider.BeginLiveRecordingCandidateSession(); int current = provider.InputCaptureSessionSerial;
        provider.SetInputCaptureOverlap(parent, 4f);
        byte[] mixed = Wav(1000, 1, 4000, 4000);
        var mixedEvidence = Evidence(current, mixed, 8f, "old echo plus user");
        Set(provider, "m_LastSingingEvidence", mixedEvidence);
        Check(provider.RetainCurrentSingingEvidenceClip(true), "mixed preview retained before ancestor verdict arrives");
        var oldPhrase = Get(candidates[candidates.Count - 1], "Phrase");
        string stableRef = (string)Get(oldPhrase, "ClipRef");
        string fullRef = (string)Get(oldPhrase, "FullClipRef");
        int sequence = (int)Get(oldPhrase, "RecordingSequence");
        Check(provider.TryPrepareSingingClip(stableRef, true, "clean", out _, out string preparationFailure),
            "preview can become prepared practice before the delayed ancestor verdict: " + preparationFailure);
        oldPhrase = practices[practices.Count - 1];
        string oldVersion = ((int)Get(oldPhrase, "Revision")).ToString();
        Check(oldVersion != "0" && (string)Get(oldPhrase, "ClipRef") == stableRef,
            "ordinary pending-to-prepared source retains clip identity and obtains an actual numeric version");
        Set(provider, "m_LastSingingAudioBytes", mixed); Set(provider, "m_LastSingingCacheEvidence", mixedEvidence);
        Set(provider, "m_LastSingingCacheCaptureSessionSerial", current);
        Set(provider, "m_RollbackSingingAudioBytes", original); Set(provider, "m_RollbackSingingCacheEvidence", otherEvidence);
        Set(provider, "m_RollbackSingingCacheCaptureSessionSerial", other);
        Set(provider, "m_LastPlayableCandidateAudioBytes", mixed); Set(provider, "m_LastPlayableCandidateEvidence", mixedEvidence);
        Call("ObserveCaptureIsolation", parent, true, "self-speech", 4f);
        Check(candidates.Count == 0 && practices.Count == 1 && ReferenceEquals(practices[0], otherPhrase),
            "late ancestor rejection revokes only polluted retained versions, preserving unrelated recorded song");
        Check(Get(provider, "m_LastSingingEvidence") == null && Get(provider, "m_LastPlayableCandidateAudioBytes") == null &&
            ReferenceEquals(Get(provider, "m_LastSingingAudioBytes"), original),
            "polluted raw/playable preview is unavailable and unrelated recent audio is restored");
        object[] crop = { current, mixed, 0f };
        Check((bool)Call("IsolateContinuationAudio", crop) && ((byte[])crop[1]).Length == 8044,
            "fresh user suffix survives invalidation and actual provider isolation");
        var suffixEvidence = Evidence(current, (byte[])crop[1], 4f, "new user suffix");
        Set(provider, "m_LastSingingEvidence", suffixEvidence);
        Check(provider.RetainCurrentSingingEvidenceClip(true), "short fresh suffix can be retained despite longer earlier preview");
        var freshPhrase = Get(candidates[0], "Phrase");
        var freshEvidence = Get(freshPhrase, "LatestRecordingEvidence") ?? Get(freshPhrase, "RecordingEvidence");
        Check((string)Get(freshPhrase, "ClipRef") != stableRef && (int)Get(freshPhrase, "RecordingSequence") == sequence &&
            ((byte[])Get(freshEvidence, "RawWavBytes")).Length == 8044 && (string)Get(freshEvidence, "Text") == "new user suffix",
            "exceptional source rejection gives fresh suffix a new clip while preserving capture event order");
        Check(!provider.TryResolveSingingClip(stableRef, out _, out _) && !provider.TryResolveSingingClip(fullRef, out _, out _),
            "both old short and full source refs are unavailable after new suffix retention");
        var chat = provider.gameObject.AddComponent<ChatSample>();
        Set(chat, "m_ChatSettings", new ChatSetting { m_SpeechToText = provider });
        string oldActual = (string)typeof(ChatSample).GetMethod("BindCurrentSingingMaterialVersions", flags)
            .Invoke(chat, new object[] { new[] { stableRef } });
        var versionCheck = typeof(ChatSample).GetMethod("SameSingingMaterialVersion", BindingFlags.Static | BindingFlags.NonPublic);
        Check(oldActual == "missing" && !(bool)versionCheck.Invoke(null, new object[] { oldVersion, oldActual }) &&
            !(bool)versionCheck.Invoke(null, new object[] { "pending", oldActual }),
            "actual production goal binding rejects old numeric and pending goals instead of promoting onto new PCM");
        object[] laterUpload = { current, mixed, 0f };
        Check((bool)Call("IsolateContinuationAudio", laterUpload) && candidates.Count == 1 &&
            ReferenceEquals(Get(candidates[0], "Phrase"), freshPhrase),
            "repeat upload isolation does not revoke already reanalysed suffix evidence");
        Set(provider, "m_RollbackSingingAudioBytes", mixed); Set(provider, "m_RollbackSingingCacheEvidence", mixedEvidence);
        Set(provider, "m_RollbackSingingCacheCaptureSessionSerial", current);
        Call("ObserveCaptureIsolation", current, true, "self-speech", 8f);
        Check(candidates.Count == 0 && Get(provider, "m_RollbackSingingAudioBytes") == null &&
            practices.Count == 1 && ReferenceEquals(Get(provider, "m_LastSingingAudioBytes"), original),
            "direct terminal rejection also revokes its rollback copy without touching other user audio");
    }
#endif
}
