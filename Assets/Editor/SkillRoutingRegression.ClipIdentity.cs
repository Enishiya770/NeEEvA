using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static partial class SkillRoutingRegression
{
    private static void RunIndependentClipIdentityRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(SenseVoiceSpeechToText);
        var host = new GameObject("IndependentClipIdentityRegression");
        host.SetActive(false);
        try
        {
            var sense = host.AddComponent<SenseVoiceSpeechToText>();
            var responseType = type.GetNestedType("Response", BindingFlags.NonPublic);
            var archive = type.GetMethod("ArchiveCompletedCapture", flags);
            var encode = type.GetMethod("EncodeMonoPcm16Wav", BindingFlags.Static | BindingFlags.NonPublic);
            byte[] Wav(float seconds) => (byte[])encode.Invoke(null, new object[] {
                Enumerable.Range(0, (int)(16000 * seconds)).Select(i => .1f * Mathf.Sin(i * .08f)).ToArray(), 16000 });
            object Response(bool combined)
            {
                var r = JsonUtility.FromJson(combined
                    ? "{\"text\":\"那我继续唱喽 初めてのルーブル モナリザ 请唱后面的歌\",\"singing_text\":\"モナリザ\",\"audio_event\":\"Speech\",\"singing_probability\":0.489,\"singing_analysis_available\":true,\"singing_start_seconds\":13.53,\"singing_end_seconds\":16.35,\"singing_recovery_start_seconds\":3.63,\"singing_recovery_end_seconds\":16.35,\"pitch_timeline_frame_seconds\":0.1,\"singing_head_extra_start_seconds\":3.63,\"singing_head_extra_end_seconds\":13.53,\"singing_head_extra_type\":\"speech\",\"singing_head_extra_text\":\"那我继续唱喽\"}"
                    : "{\"text\":\"那我继续唱喽\",\"audio_event\":\"Speech\",\"singing_probability\":0.2,\"singing_analysis_available\":true,\"singing_start_seconds\":0,\"singing_end_seconds\":5.37,\"singing_recovery_start_seconds\":0,\"singing_recovery_end_seconds\":5.37,\"pitch_timeline_frame_seconds\":0.1}", responseType);
                responseType.GetField("pitch_timeline_midi").SetValue(r, Enumerable.Repeat(60f, combined ? 164 : 54).ToArray());
                return r;
            }
            void Archive(bool combined, int session) => archive.Invoke(sense, new object[] {
                Response(combined), Wav(combined ? 16.35f : 5.37f), 10f, session });
            sense.BeginLiveRecordingCandidateSession();
            Archive(false, 1);
            sense.BeginLiveRecordingCandidateSession();
            sense.SetInputCaptureOverlap(1, 5.37f);
            Archive(true, 2); // Deliberately identical timestamp, independent capture.
            var candidates = sense.DescribeQuarantinedSingingCandidates();
            string first = candidates.Single(p => p.RecordingSequence == 1).ClipRef;
            string second = candidates.Single(p => p.RecordingSequence == 2).ClipRef;
            var records = (IList)type.GetField("m_QuarantinedSingingCandidates", flags).GetValue(sense);
            object phrase = records[1].GetType().GetField("Phrase").GetValue(records[1]);
            string legacySecond = (string)phrase.GetType().GetField("FullClipRef").GetValue(phrase);
            if (first == second || first.Length >= 37 || second.Length >= 37 || legacySecond.Length != 37 ||
                sense.CanonicalSingingClipReference(legacySecond) != second ||
                sense.TryResolveSingingClip(second + "0", out _, out _) ||
                sense.TryResolveSingingClip(first.Substring(0, first.Length - 1), out _, out _))
                throw new Exception("Short clip handles are ambiguous, mutable, or lose exact legacy identity.");
            string observation = sense.DescribeSingingClipObservation(second);
            if (!observation.Contains("shared_prefix_raw=[0,5.37]") || !observation.Contains("earlier_ref=" + first) ||
                !observation.Contains("speaker_continuity=unknown") || !observation.Contains("请唱后面的歌"))
                throw new Exception("Joint ASR hid the reused prefix, whole transcript or uncertain speaker continuity.");
            var chat = host.AddComponent<ChatSample>();
            typeof(ChatSample).GetField("m_ChatSettings", flags).SetValue(chat, new ChatSetting { m_SpeechToText = sense });
            string overview = (string)typeof(ChatSample).GetMethod("BuildSingingClipOverview", flags).Invoke(chat, new object[] { sense });
            if (!overview.Contains("acoustic_mode=speech") || !overview.Contains("singing_p=0.200") ||
                !overview.Contains("recording_transcript=\"那我继续唱喽\"") || !overview.Contains("不是歌唱判定"))
                throw new Exception("Playable speech is still presented as established singing.");
            if (!sense.TryPrepareSingingClip(first, true, "current", out int firstStable, out string error))
                throw new Exception(error);
            if (sense.TrySelectSingingClipWindow(legacySecond, true, 3.63f, 16.35f, true, out _, out _) ||
                sense.TrySelectSingingClipWindow(second, true, 13.53f, 20f, false, out _, out _))
                throw new Exception("Invalid or speech-conflicting candidate range was admitted.");
            var untouched = sense.DescribeQuarantinedSingingCandidates().Single();
            if (untouched.ClipRef != second || untouched.SourceStatus != "pending" ||
                untouched.PlaybackStatus != "evidence_only" || sense.PracticePhraseCount != 1)
                throw new Exception("Failed range mutated/deleted a candidate or confirmed source.");
            if (!sense.TrySelectSingingClipWindow(legacySecond, false, 13.53f, 16.35f, true,
                    out int secondStable, out error)) throw new Exception(error);
            var phrases = sense.DescribePracticePhrases();
            if (phrases.Count != 2 || firstStable == secondStable ||
                Math.Abs(phrases.Single(p => p.ClipRef == first).Seconds - 5.37f) > .02f ||
                Math.Abs(phrases.Single(p => p.ClipRef == second).Seconds - 2.82f) > .02f ||
                !phrases.Single(p => p.ClipRef == second).PendingConfirmation ||
                sense.CanonicalSingingClipReference(legacySecond) != second)
                throw new Exception("Admission replaced speech with singing, rewrote refs, or copied source confirmation.");
            Archive(true, 2);
            if (sense.PracticePhraseCount != 2 || sense.DescribeQuarantinedSingingCandidates().Count != 0)
                throw new Exception("Final evidence duplicated an admitted recording.");
            // One resolver is shared by current/legacy paths; mixed aliases are deduplicated on deletion.
            if (!sense.ConfirmSingingClipSource(legacySecond, out _) ||
                !sense.TryPrepareSingingClip(legacySecond, false, "clean", out _, out error)) throw new Exception(error);
            object[] dropArgs = { "<clip_drop ref=\"" + second + "\"/><clip_drop ref=\"" + legacySecond + "\"/>", true };
            typeof(ChatSample).GetMethod("ExtractAndApplyClipDropTags", flags).Invoke(chat, dropArgs);
            if (sense.TryResolveSingingClip(legacySecond, out _, out _) || sense.TryResolveSingingClip(second, out _, out _) ||
                !sense.TryResolveSingingClip(first, out _, out _)) throw new Exception("Drop aliased another recording or left a stale alias.");
            sense.BeginLiveRecordingCandidateSession();
            Archive(true, 3);
            string third = sense.DescribeQuarantinedSingingCandidates().Single().ClipRef;
            if (third == second || sense.TryResolveSingingClip(second, out _, out _))
                throw new Exception("Removed short handles were reused.");
            Debug.Log("[SkillRoutingRegression] independent captures, shared-prefix evidence, short aliases and staged candidate admission passed");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunLoadedSkillRequestRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = new GameObject("LoadedSkillRequestRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var type = typeof(ChatSample);
            var requestType = type.GetNestedType("AgentSkillRequest", BindingFlags.NonPublic);
            var request = Activator.CreateInstance(requestType);
            requestType.GetField("Name").SetValue(request, "singing");
            var route = type.GetMethod("GetSkillRouteState", flags).Invoke(chat, new object[] { "singing" });
            var routeType = route.GetType();
            routeType.GetField("ActiveThisRound").SetValue(route, true);
            routeType.GetField("LastAutonomousGrantAt").SetValue(route, 123f);
            var loaded = (HashSet<string>)type.GetField("m_ActiveSkillsThisRound", flags).GetValue(chat);
            loaded.Add("singing");
            var resolve = type.GetMethod("ResolveAutonomousSkillRequest", flags);
            string Resolve() => resolve.Invoke(chat, new object[] { request, "" }).ToString();
            for (int i = 0; i < 3; i++)
                if (Resolve() != "AlreadyLoaded") throw new Exception("Loaded skill request isn't idempotent.");
            type.GetField("m_ChatSettings", flags).SetValue(chat, new ChatSetting());
            type.GetMethod("OnStreamComplete", flags).Invoke(chat, new object[] { "<skill_request name=\"singing\"/>" });
            if ((bool)type.GetField("m_ToolCorrectionContinuationPending", flags).GetValue(chat) ||
                (int)type.GetField("m_ToolCorrectionAttemptsThisUserTurn", flags).GetValue(chat) != 0 ||
                !string.IsNullOrEmpty((string)type.GetField("m_PendingAutonomousSkillName", flags).GetValue(chat)) ||
                (float)routeType.GetField("LastAutonomousGrantAt").GetValue(route) != 123f ||
                !((string)type.GetField("m_PendingSkillStatusFrame", flags).GetValue(chat)).Contains("already_loaded"))
                throw new Exception("Loaded request starts correction, grants/renews a skill, or lacks a truthful status.");
            requestType.GetField("Name").SetValue(request, "nonexistent");
            if (Resolve() != "Rejected") throw new Exception("Unknown skill was accepted.");
            requestType.GetField("Name").SetValue(request, "singing");
            loaded.Clear();
            routeType.GetField("ActiveThisRound").SetValue(route, false);
            if (Resolve() != "Rejected") throw new Exception("Unloaded skill bypassed the current mode restriction.");
            type.GetField("m_AgentRunning", flags).SetValue(chat, true);
            routeType.GetField("LastAutonomousGrantAt").SetValue(route, -99999f);
            if (Resolve() != "Granted") throw new Exception("A valid new autonomous skill request no longer grants.");
            Debug.Log("[SkillRoutingRegression] loaded skill request idempotence and real grant/rejection paths passed");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
}
