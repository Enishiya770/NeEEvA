using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using System.Collections;
using UnityEngine;
#endif

public static class RetainedRecordingPcmRegression
{
    private static int count;
    private static void Check(bool okay, string reason) { count++; if (!okay) throw new InvalidOperationException("Retained PCM: " + reason); }
    private static bool Restore(byte[] bytes, JObject mapping) => (bool)typeof(WaveformTrace)
        .GetMethod("RestoreRetainedMapping", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { bytes, mapping });
    private static void Observe(byte[] bytes, Action<JObject> receive) => typeof(WaveformTrace)
        .GetMethod("ObserveRetainedMapping", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { bytes, receive });
    private static byte[] Wav(int identity)
    {
        using (var buffer = new MemoryStream()) using (var w = new BinaryWriter(buffer)) {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + 64000); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(64000);
            for (int i = 0; i < 32000; i++) w.Write((short)(Math.Sin(i * .08) * 6000 + identity));
            return buffer.ToArray();
        }
    }
    public static int RunChecks()
    {
        count = 0;
        string directory = Path.Combine(Path.GetTempPath(), "NeEEvA-retained-pcm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var store = new RetainedRecordingPcmStore(Path.Combine(directory, "archive"), 130000);
            var retained = new List<RetainedRecordingPcm>(); var originals = new List<JObject>();
            int evictions = 0, restores = 0; bool bounded = true;
            for (int i = 1; i <= 160; i++) {
                byte[] bytes = Wav(i); WaveformTrace.RegisterInput(bytes, i);
                JObject map = WaveformTrace.Describe(bytes); originals.Add((JObject)map.DeepClone());
                var item = store.Put(bytes);
                item.BeforeEvict = value => { map = WaveformTrace.Describe(value); evictions++; };
                item.Restore = value => { Check(Restore(value, map), "hash-bound mapping restored"); restores++; };
                retained.Add(item); bounded &= store.ResidentBytes <= 130000;
            }
            Check(bounded && evictions >= 158, "160 full PCM sources exceed cache without exceeding resident bound");
            Check(retained.All(x => x.Available && x.ByteCount == 64044), "metadata availability does not load old PCM");
            int before = restores;
            Check(retained.Take(100).All(x => x.Available) && restores == before, "100-source metadata listing causes no disk reads");
            byte[] first = retained[0].Read();
            Check(first.SequenceEqual(Wav(1)), "source 1 survives more than 64 and digest-cache capacity 128");
            Check(JToken.DeepEquals(WaveformTrace.Describe(first)["regions"], originals[0]["regions"]), "old source PCM provenance survives disk reload");
            Check(!Restore(first, originals[1]), "wrong PCM cannot acquire another retained source mapping");
            Check(retained[159].Read().SequenceEqual(Wav(160)) && store.ResidentBytes <= 130000, "new and old sources both available with bounded residency");
            var oversize = new RetainedRecordingPcmStore(Path.Combine(directory, "oversize"), 1);
            var one = oversize.Put(Wav(5));
            Check(one.Available && oversize.ResidentBytes == 0 && one.Read().SequenceEqual(Wav(5)), "oversize WAV remains disk backed without resident-cache admission");
            byte[] lateBytes = Wav(161); JObject lateMapping = WaveformTrace.Describe(lateBytes);
            var late = oversize.Put(lateBytes);
            Observe(lateBytes, value => lateMapping = value);
            late.Restore = value => Restore(value, lateMapping);
            WaveformTrace.RegisterInput(lateBytes, 161);
            Check((int)WaveformTrace.Describe(late.Read())["regions"][0]["captureSerial"] == 161,
                "late registration survives a WAV that never entered resident cache");
            File.WriteAllBytes((string)typeof(RetainedRecordingPcm).GetField("Path", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(one), new byte[] { 1, 2, 3 });
            Check(one.Read() == null && one.UnavailableReason == "archive-pcm-integrity-failed" && !one.Available, "corrupted archived PCM cannot be played as original");
            string obstructed = Path.Combine(directory, "not-a-directory"); File.WriteAllText(obstructed, "blocked");
            var failed = new RetainedRecordingPcmStore(obstructed, 65000);
            var fallback = failed.Put(Wav(6)); var unavailable = failed.Put(Wav(7));
            Check(fallback.Available && fallback.Read().SequenceEqual(Wav(6)), "disk failure retains bounded fallback without deleting old source");
            Check(!unavailable.Available && unavailable.Read() == null && failed.ResidentBytes <= 65000 && failed.LastError != null, "disk failure beyond capacity explicit, not silent FIFO loss");
            return count;
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
#if UNITY_EDITOR
    public static int RunProviderChecks()
    {
        count = 0;
        var go = new GameObject("Archive retention regression"); go.SetActive(false);
        try {
            var provider = go.AddComponent<SenseVoiceSpeechToText>(); var type = typeof(SenseVoiceSpeechToText);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var evidenceType = type.GetNestedType("SingingEvidenceSnapshot", BindingFlags.NonPublic);
            var clips = new List<string>(); bool allRetained = true;
            for (int i = 1; i <= 120; i++) {
                provider.BeginLiveRecordingCandidateSession(); int capture = provider.InputCaptureSessionSerial;
                var evidence = Activator.CreateInstance(evidenceType, true);
                void Set(string name, object value) { var field = evidenceType.GetField(name, flags); if (field != null) field.SetValue(evidence, value); else evidenceType.GetProperty(name, flags).SetValue(evidence, value); }
                byte[] wav = Wav(i); WaveformTrace.RegisterInput(wav, capture);
                Set("CaptureSessionSerial", capture); Set("RawWavBytes", wav); Set("RawSeconds", 2f);
                Set("Text", "ordinary introduction and recording " + i); Set("SingingText", "retained phrase " + i); Set("Language", "zh");
                Set("PitchTimelineMidi", Enumerable.Repeat(60f, 20).ToArray()); Set("FrameSeconds", .1f);
                Set("CleanEndSeconds", 2f); Set("RecoveryEndSeconds", 2f);
                type.GetField("m_LastSingingEvidence", flags).SetValue(provider, evidence);
                allRetained &= provider.RetainCurrentSingingEvidenceClip(true);
                var candidates = (IList)type.GetField("m_QuarantinedSingingCandidates", flags).GetValue(provider);
                object last = candidates[candidates.Count - 1]; object phrase = last.GetType().GetField("Phrase", flags).GetValue(last);
                clips.Add((string)phrase.GetType().GetField("ClipRef", flags).GetValue(phrase));
            }
            Check(allRetained && clips.Distinct().Count() == 120, "120 quarantine entries retain distinct identities");
            Check(((IList)type.GetField("m_QuarantinedSingingCandidates", flags).GetValue(provider)).Count == 120, "quarantine no longer silently evicts after 64");
            Check(provider.DescribeRecordingSingingDecisionFacts(clips[0]) != null, "oldest candidate remains inspectable");
            bool prepared = true; string problem = null;
            foreach (string clip in clips) { bool okay = provider.TryPrepareSingingClip(clip, true, "clean", out _, out string error); prepared &= okay; if (!okay) problem = error; }
            Check(prepared, "all original sources prepare: " + problem);
            Check(((IList)type.GetField("m_PracticePhrases", flags).GetValue(provider)).Count == 120, "prepared archive no longer silently evicts after 64");
            Check(provider.TryResolveSingingClip(clips[0], out _, out _) && provider.TryResolveSingingClip(clips[119], out _, out _), "early and latest stable references remain resolvable");
            var fact = JObject.FromObject(provider.DescribeRecordingSingingDecisionFacts(clips[0]));
            Check((bool)fact["RawAudioRetained"] && (bool)fact["PreparedAudioAvailable"] && (long)fact["RawAudioByteCount"] == 64044, "provider availability metadata reflects actual retained source");
            return count;
        } finally { UnityEngine.Object.DestroyImmediate(go); }
    }
#endif
}
