using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static partial class SkillRoutingRegression
{
    private static void RunRecordingEvidenceRetentionRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(SenseVoiceSpeechToText);
        var host = new GameObject("RecordingEvidenceRetentionRegression");
        host.SetActive(false);
        try
        {
            var sense = host.AddComponent<SenseVoiceSpeechToText>();
            var responseType = type.GetNestedType("Response", BindingFlags.NonPublic);
            var archive = type.GetMethod("ArchiveCompletedCapture", flags);
            var encode = type.GetMethod("EncodeMonoPcm16Wav", BindingFlags.Static | BindingFlags.NonPublic);
            byte[] Wav(float seconds) => (byte[])encode.Invoke(null, new object[] {
                Enumerable.Range(0, (int)(16000 * seconds)).Select(i => .1f * Mathf.Sin(i * .08f)).ToArray(), 16000 });
            object Response(bool final)
            {
                object response = JsonUtility.FromJson(final
                    ? "{\"text\":\"ルーブル モナリザ 最后的请求\",\"singing_text\":\"ルーブル モナリザ\",\"singing_probability\":0.489,\"singing_analysis_available\":true,\"singing_start_seconds\":2.13,\"singing_end_seconds\":8.57,\"singing_recovery_start_seconds\":2.13,\"singing_recovery_end_seconds\":11.17,\"pitch_timeline_frame_seconds\":0.1,\"singing_tail_extra_start_seconds\":8.57,\"singing_tail_extra_end_seconds\":11.17,\"singing_tail_extra_type\":\"speech\",\"singing_tail_extra_text\":\"最后的请求\"}"
                    : "{\"text\":\"ルーブル\",\"singing_text\":\"ルーブル\",\"singing_probability\":0.571,\"singing_analysis_available\":true,\"singing_start_seconds\":2.13,\"singing_end_seconds\":7.06,\"singing_recovery_start_seconds\":2.13,\"singing_recovery_end_seconds\":7.06,\"pitch_timeline_frame_seconds\":0.1}", responseType);
                responseType.GetField("pitch_timeline_midi").SetValue(response,
                    Enumerable.Repeat(60f, final ? 112 : 71).ToArray());
                return response;
            }
            byte[] preview = Wav(7.06f), full = Wav(11.17f);
            object early = Response(false), finalResponse = Response(true);
            void Archive(object response, byte[] wav, int session) =>
                archive.Invoke(sense, new object[] { response, wav, 10f + session, session });
            sense.BeginLiveRecordingCandidateSession();
            sense.BeginLiveRecordingCandidateSession();
            // The second recording finishes processing first: sequence was reserved at capture start.
            Archive(early, preview, 2);
            Archive(early, preview, 1);
            var candidates = sense.DescribeQuarantinedSingingCandidates();
            string first = candidates.Single(p => p.RecordingSequence == 1).ClipRef;
            string second = candidates.Single(p => p.RecordingSequence == 2).ClipRef;
            Archive(finalResponse, full, 1);
            Archive(early, preview, 1); // late short result must not overwrite the complete recording
            if (sense.DescribeQuarantinedSingingCandidates().Count != 2 ||
                Math.Abs(sense.DescribeQuarantinedSingingCandidates().Single(p => p.ClipRef == first).Seconds - 4.93f) > .02f)
                throw new Exception("Final evidence duplicated a recording or automatically expanded current audio.");
            string latest = sense.DescribeLatestSingingRecordingEvidence(first);
            if (!latest.Contains("11.17") || !latest.Contains("モナリザ") || !latest.Contains("0.489") || !latest.Contains("最后的请求"))
                throw new Exception("Weaker final recording or complete transcript was hidden by the early playable cache.");
            if (!sense.TryPrepareSingingClip(first, false, "current", out int stable, out string error))
                throw new Exception("Source-pending playable selection still needs confirmation: " + error);
            var current = sense.DescribePracticePhrases().Single();
            if (!current.PendingConfirmation || current.RecordingSequence != 1 || Math.Abs(current.Seconds - 4.93f) > .02f ||
                !sense.DescribeLatestSingingRecordingEvidence(first).Contains("11.17"))
                throw new Exception("Admission changed provenance, current audio, sequence, or discarded full evidence.");
            int revision = current.Revision;
            if (sense.TrySelectSingingClipWindow(first, false, 2.13f, 11.17f, true, out _, out _) ||
                sense.TrySelectSingingClipWindow(first, false, 2.13f, 20f, false, out _, out _) ||
                sense.DescribePracticePhrases().Single().Revision != revision)
                throw new Exception("Invalid/speech-conflicting latest-evidence range changed current audio.");
            if (!sense.TryPrepareSingingClip(first, false, "expanded", out _, out error))
                throw new Exception("Explicit expanded selection did not use full evidence: " + error);
            current = sense.DescribePracticePhrases().Single();
            if (Math.Abs(current.Seconds - 9.04f) > .02f || current.RawSeconds < 11.16f ||
                !current.PendingConfirmation || current.RecordingSequence != 1 || current.Revision <= revision)
                throw new Exception("Explicit full-evidence selection lost audio, provenance, or version identity.");
            // Updating an existing practice entry, not only a quarantined candidate, must retain evidence too.
            if (!sense.TryPrepareSingingClip(second, false, "current", out _, out _)) throw new Exception("Second clip failed.");
            Archive(finalResponse, full, 2);
            if (!sense.DescribeLatestSingingRecordingEvidence(second).Contains("モナリザ"))
                throw new Exception("Existing practice entry dropped newer recording evidence.");
            // Candidate recovery also uses latest raw coordinates without automatically confirming source.
            sense.BeginLiveRecordingCandidateSession();
            Archive(early, preview, 3);
            Archive(finalResponse, full, 3);
            string third = sense.DescribeQuarantinedSingingCandidates().Single().ClipRef;
            if (sense.TrySelectSingingClipWindow(third, false, 10f, 20f, false, out _, out _) ||
                !sense.TrySelectSingingClipWindow(third, false, 2.13f, 8.57f, true, out _, out error))
                throw new Exception("Candidate latest-evidence raw range failed: " + error);
            current = sense.DescribePracticePhrases().Single(p => p.ClipRef == third);
            if (!current.PendingConfirmation || current.RecordingSequence != 3 || Math.Abs(current.Seconds - 6.44f) > .02f)
                throw new Exception("Candidate range selection confirmed provenance or used stale evidence.");
            if (!sense.ConfirmSingingClipSource(third, out _) ||
                sense.DescribePracticePhrases().Single(p => p.ClipRef == third).PendingConfirmation)
                throw new Exception("Optional explicit stable-clip source confirmation failed.");
            if (!sense.DropPracticePhraseByStableId(stable, out _, out _, out _)) throw new Exception("Drop failed.");
            Archive(finalResponse, full, 1);
            if (sense.TryResolveSingingClip(first, out _, out _) ||
                sense.DescribePracticePhrases().Single(p => p.ClipRef == second).RecordingSequence != 2 ||
                sense.DescribePracticePhrases().Single(p => p.ClipRef == third).RecordingSequence != 3)
                throw new Exception("A dropped clip resurrected or remaining recordings were renumbered.");
            Debug.Log("[SkillRoutingRegression] pending playback, complete recording evidence and immutable recording sequences passed");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
}
