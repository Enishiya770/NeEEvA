using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Actual inactive providers/context adapter. Synthetic PCM represents retained
// fixtures only; no recognition, network, microphone, sound, scene or service edit.
public static class FastLoopContextRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static void RunBatch()
    {
        var report = new JObject { ["passed"] = false };
        try { report["checks"] = RunOrThrow(); report["passed"] = true; }
        catch (Exception e) { report["error"] = e.ToString(); Debug.LogException(e); }
        string path = Path.GetFullPath("Logs/fast-loop-context-regression/unity-context.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString());
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    public static int RunOrThrow()
    {
        checks = 0;
        var archiveRows = new JArray(Enumerable.Range(1, 20).Select(i => new JObject {
            ["clipRef"] = "clip:test-" + i, ["recordingSequence"] = i,
            ["sourceStatus"] = i == 1 ? "confirmed_user" : "pending",
            ["playbackStatus"] = i == 1 ? "ready" : "evidence_only",
            ["transcriptExcerpt"] = i == 2 ? "已保留的第二段示范" : "后续澄清" }));
        var retainedIndex = FastLoopPrompt.RecordingMaterials(archiveRows, new[] { "clip:test-1" }, true,
            new[] { "clip:unknown-2", "clip:test-2" });
        Check(retainedIndex["recent"].Any(v => (string)v["clipRef"] == "clip:test-2" &&
            (string)v["retainedForContext"] == "review-expected-reference" && (string)v["playbackStatus"] == "evidence_only"),
            "An independently expected older source survives many clarification recordings without gaining playback approval.");
        Check((int)retainedIndex["totalRetained"] == 20 && (int)retainedIndex["pendingCount"] == 19 &&
            (int)retainedIndex["playableCount"] == 1 && retainedIndex["recent"].Count() <= 6 &&
            !retainedIndex["recent"].Any(v => (string)v["clipRef"] == "clip:unknown-2"),
            "Compact inventory reports retained and ready states separately; a review cannot invent an absent source.");
        var fixture = JObject.Parse(File.ReadAllText("Tools/FastLoopContext/latest-session.json"));
        var host = new GameObject("Inactive exact-input and singing-context regression"); host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>(); var sense = host.AddComponent<SenseVoiceSpeechToText>();
            Set(chat, "m_ChatSettings", new ChatSetting { m_SpeechToText = sense });
            Set(chat, "m_LogAgentLoop", false); Set(chat, "m_SingingGoalTurn", 18);
            for (int i = 0; i < 18; ++i) sense.BeginLiveRecordingCandidateSession();
            string songRef = null;
            foreach (JObject row in fixture["rows"].Where(v => (int)v["capture"] >= 19))
            {
                int capture = (int)row["capture"]; string text = (string)row["user"];
                Stage(sense, text, capture == 19 ? fixture["segments"] as JArray : null, capture == 19 ? 21.16f : 2.4f);
                Call(chat, "RetainCompletedRecordingEvidence", sense);
                string evidence = "[fixture accepted program evidence] " + text;
                Set(chat, "m_LastUserMsg", text); Set(chat, "m_LastUserEvidence", evidence);
                Call(chat, "ResetSingingGoalForUserTurn");
                Call(chat, "ResetAcceptedRecordingInput");
                Call(chat, "BindAcceptedRecordingInput", true);
                Set(chat, "m_FormalResponseGeneration", capture); Set(chat, "m_WorkEpoch", capture);
                Check((string)Call(chat, "FastLoopCurrentUserText", evidence) == text, "complete accepted final " + capture);
                var facts = Facts(chat); var current = facts["inputObservation"]["acoustic"];
                Check((int)current["captureSessionSerial"] == capture && (bool)facts["inputObservation"]["acceptedInputBound"], "exact capture origin " + capture);
                if (capture == 19)
                {
                    songRef = (string)current["clipRef"];
                    Check(current["timeOrder"]["segments"].Count() == 4 && (bool)current["timeOrder"]["recordingEnded"], "real adapter projects all mixed segments");
                    Check(((string)current["timeOrder"]["segments"].Last["text"]).EndsWith("唱给我听吗？"), "tail request is present");
                    Check(facts["recordingMaterials"]["recent"].Single(v => (string)v["clipRef"] == songRef)["timeOrder"] == null,
                        "current timeline emitted once");
                    var detached = JObject.FromObject(sense.DescribeTimeOrderedTranscriptForFinalInput(sense.LastText));
                    detached["segments"][0]["text"] = "changed snapshot";
                    Check((string)JObject.FromObject(sense.DescribeTimeOrderedTranscriptForFinalInput(sense.LastText))["segments"][0]["text"] != "changed snapshot", "read-only source snapshot detached");
                }
                var retained = facts["recordingMaterials"]["recent"].Single(v => (string)v["clipRef"] == songRef);
                Check((string)retained["sourceStatus"] == "pending" && (string)retained["playbackStatus"] == "evidence_only" &&
                    (string)retained["revision"] == "pending", "context never approves singing " + capture);
                Check(sense.PracticePhraseCount == 0 && Get(chat, "m_SingingGoal") == null, "no source promotion or invented target " + capture);
                if (capture > 19)
                {
                    Check(current["timeOrder"].Type == JTokenType.Null, "ordinary clarification is not a singing timeline " + capture);
                    Check((int)retained["observedCaptureSessionSerial"] == 19 && retained["timeOrder"]["segments"].Count() == 4,
                        "candidate19 remains available through " + capture);
                }
                if (capture == 24)
                    Check(((string)JObject.Parse(FastLoopPrompt.User(facts.ToString(Formatting.None), text))["current_user"]).EndsWith("那一段歌说错了。"),
                        "actual self-correction tail survives production serialization");
            }
            Type goalType = typeof(ChatSample).GetNestedType("SingingGoal", Flags);
            object goal = Activator.CreateInstance(goalType, true);
            goalType.GetField("refs").SetValue(goal, songRef); goalType.GetField("intent").SetValue(goal, "perform");
            goalType.GetField("review").SetValue(goal, "approved");
            Set(chat, "m_ActiveSingingGoal", goal);
            var activeMaterial = Facts(chat)["recordingMaterials"]["recent"].Single(v => (string)v["clipRef"] == songRef);
            Check((string)activeMaterial["retainedForContext"] == "active-goal-reference" && (string)activeMaterial["sourceStatus"] == "pending",
                "exact active target retained without using goal approval as source approval");
            goalType.GetField("refs").SetValue(goal, "clip:foreign-session-19");
            Check(!Facts(chat)["recordingMaterials"]["recent"].Any(v => (string)v["retainedForContext"] == "active-goal-reference"),
                "unknown goal identity cannot alias same recording number");
            Set(chat, "m_ActiveSingingGoal", null);
            string currentEvidence = (string)Get(chat, "m_LastUserEvidence");
            Set(chat, "m_FormalResponseGeneration", 25);
            Check(Facts(chat)["inputObservation"]["acoustic"].Type == JTokenType.Null, "new generation fences old audio facts");
            Call(chat, "FastLoopCurrentUserText", currentEvidence); Set(chat, "m_WorkEpoch", 25);
            Check(Facts(chat)["inputObservation"]["acoustic"].Type == JTokenType.Null, "new work epoch fences old audio facts");
            Call(chat, "FastLoopCurrentUserText", currentEvidence); Set(chat, "m_LastUserEvidence", currentEvidence + " modified");
            Check(Facts(chat)["inputObservation"]["acoustic"].Type == JTokenType.Null, "transaction mismatch fences snapshot");
            Set(chat, "m_LastUserEvidence", currentEvidence); Set(chat, "m_CurrentUserInputHasAudioEvidence", false);
            Call(chat, "FastLoopCurrentUserText", currentEvidence);
            Check(Facts(chat)["inputObservation"]["acoustic"].Type == JTokenType.Null, "typed identical words cannot claim audio");
            Set(chat, "m_CurrentUserInputHasAudioEvidence", true); Set(chat, "m_AgentCurrentRoundIsTick", true);
            Check((string)Call(chat, "FastLoopCurrentUserText", currentEvidence) == currentEvidence &&
                Facts(chat)["inputObservation"]["acoustic"].Type == JTokenType.Null, "autonomous tick cannot consume old user input");
            Set(chat, "m_AgentCurrentRoundIsTick", false);

            string songText = (string)fixture["rows"].Single(v => (int)v["capture"] == 19)["user"];
            Stage(sense, songText, fixture["segments"] as JArray, 21.16f); Call(chat, "RetainCompletedRecordingEvidence", sense);
            Check(sense.DescribeTimeOrderedTranscriptForFinalInput(songText) != null, "matching new singing source available");
            int oldSource = sense.InputCaptureSessionSerial;
            Stage(sense, songText, null, 21.16f); Call(chat, "RetainCompletedRecordingEvidence", sense);
            // Simulate the old text-only bool being true in an identical-text new capture.
            SetSegments(sense, fixture["segments"] as JArray, songText, oldSource);
            Check(sense.HasTimeOrderedTranscript && sense.DescribeRetainedRecordingForFinalInput(songText) != null &&
                sense.DescribeTimeOrderedTranscriptForFinalInput(songText) == null, "same text in different capture cannot reuse old segments");
            Check(sense.DescribeTimeOrderedTranscriptForFinalInput(songText + "changed") == null, "exact final text required");
            ((ChatSetting)Get(chat, "m_ChatSettings")).m_SpeechToText = host.AddComponent<SenseVoiceSpeechToText>();
            Check(Facts(chat)["recordingMaterials"]["recent"].Count() == 0 && ((IList)Get(chat, "m_FastLoopRecentSingingObservations")).Count == 0,
                "provider switch drops old reference cache");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks;
    }

    private static JObject Facts(ChatSample chat) { var facts = new JObject(); Call(chat, "EnrichFastLoopContext", facts); return facts; }
    private static void Stage(SenseVoiceSpeechToText sense, string text, JArray segments, float seconds)
    {
        sense.BeginLiveRecordingCandidateSession();
        Type type = typeof(SenseVoiceSpeechToText), evidenceType = type.GetNestedType("SingingEvidenceSnapshot", Flags);
        object evidence = Activator.CreateInstance(evidenceType, true);
        void Field(string name, object value) { if (name == "RawWavBytes") evidenceType.GetProperty(name).SetValue(evidence, value); else evidenceType.GetField(name).SetValue(evidence, value); }
        byte[] wav = (byte[])type.GetMethod("EncodeMonoPcm16Wav", Flags).Invoke(null,
            new object[] { new float[Mathf.RoundToInt(seconds * 16000)], 16000 });
        Field("CaptureSessionSerial", sense.InputCaptureSessionSerial); Field("RawWavBytes", wav);
        Field("RawSeconds", seconds); Field("CleanStartSeconds", 0f); Field("CleanEndSeconds", seconds);
        Field("RecoveryStartSeconds", 0f); Field("RecoveryEndSeconds", seconds);
        // Nonempty unvoiced timeline exercises the actual evidence_only branch;
        // an absent timeline correctly yields unavailable in the production archive.
        Field("FrameSeconds", .1f); Field("PitchTimelineMidi", new float[Mathf.CeilToInt(seconds / .1f)]);
        Field("AtRealtime", Time.realtimeSinceStartup); Field("Text", text); Field("SingingText", "");
        Field("AcousticMode", "uncertain"); Field("AudioEvent", "Speech");
        Set(sense, "m_LastSingingEvidence", evidence);
        type.GetProperty("LastText", Flags).SetValue(sense, text);
        SetSegments(sense, segments, text, sense.InputCaptureSessionSerial);
    }
    private static void SetSegments(SenseVoiceSpeechToText sense, JArray segments, string text, int capture)
    {
        Type type = typeof(SenseVoiceSpeechToText).GetNestedType("TurnTranscriptSegment", Flags);
        Set(sense, "m_LastTurnSegments", segments == null ? null : JsonConvert.DeserializeObject(segments.ToString(), type.MakeArrayType()));
        Set(sense, "m_LastSegmentedPrimaryText", text); Set(sense, "m_LastTurnSegmentsCaptureSessionSerial", capture);
    }
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
}
