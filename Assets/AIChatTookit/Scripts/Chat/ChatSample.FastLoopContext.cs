using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_FastLoopInputFactsGeneration = -1, m_FastLoopInputFactsEpoch = -1;
    private string m_FastLoopInputFactsText, m_FastLoopInputFactsEvidence;
    private SenseVoiceSpeechToText m_FastLoopInputFactsProvider, m_FastLoopMaterialProvider;
    private int m_FastLoopInputFactsCapture;
    private long m_FastLoopAcceptedInputRevision = -1;
    private JObject m_FastLoopAcceptedAudioFacts;
    private readonly List<JObject> m_FastLoopRecentSingingObservations = new List<JObject>();

    // Only a byte-for-byte match to the accepted input transaction can separate
    // its transcript from the program-authored auditory wrapper. Direct/synthetic
    // prompts, autonomous ticks and empty continuations keep their own input.
    private string FastLoopCurrentUserText(string fallback)
    {
        m_FastLoopInputFactsGeneration = m_FastLoopInputFactsEpoch = -1;
        m_FastLoopInputFactsText = m_FastLoopInputFactsEvidence = null;
        m_FastLoopAcceptedAudioFacts = null;
        m_FastLoopInputFactsProvider = null;
        m_FastLoopInputFactsCapture = 0;
        m_FastLoopAcceptedInputRevision = m_AcceptedRecordingInputRevision;
        if (!FastLoopPrompt.TryAcceptedCurrentUser(fallback, m_LastUserEvidence, m_LastUserMsg,
            m_AgentCurrentRoundIsTick, out string currentUser)) return currentUser;
        m_FastLoopInputFactsGeneration = m_FormalResponseGeneration;
        m_FastLoopInputFactsEpoch = m_WorkEpoch;
        m_FastLoopInputFactsText = m_LastUserMsg;
        m_FastLoopInputFactsEvidence = m_LastUserEvidence;
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        // Equal words alone cannot make a typed input or a different recording audio.
        if (m_CurrentUserInputHasAudioEvidence && sense != null && AcceptedRecordingInputCurrent &&
            m_AcceptedAudioFacts != null && ReferenceEquals(sense, m_AcceptedRecordingInputProvider))
        {
                m_FastLoopInputFactsProvider = sense;
                m_FastLoopInputFactsCapture = (int)m_AcceptedAudioFacts["captureSessionSerial"];
                m_FastLoopAcceptedAudioFacts = (JObject)m_AcceptedAudioFacts.DeepClone();
                RememberFastLoopSingingObservation(sense);
        }
        return currentUser;
    }

    private bool FastLoopInputFactsCurrent =>
        m_FastLoopInputFactsGeneration == m_FormalResponseGeneration &&
        m_FastLoopInputFactsEpoch == m_WorkEpoch && !m_AgentCurrentRoundIsTick &&
        m_FastLoopAcceptedInputRevision == m_AcceptedRecordingInputRevision &&
        string.Equals(m_FastLoopInputFactsText, m_LastUserMsg, StringComparison.Ordinal) &&
        string.Equals(m_FastLoopInputFactsEvidence, m_LastUserEvidence, StringComparison.Ordinal) &&
        (m_FastLoopAcceptedAudioFacts == null || m_CurrentUserInputHasAudioEvidence &&
            ReferenceEquals(m_FastLoopInputFactsProvider, m_ChatSettings?.m_SpeechToText) &&
            AcceptedRecordingInputCurrent &&
            (int?)m_AcceptedRecordingIdentity?["capture"] == m_FastLoopInputFactsCapture);

    private void RememberFastLoopSingingObservation(SenseVoiceSpeechToText sense)
    {
        PruneFastLoopSingingObservations(sense);
        if (!FastLoopPrompt.HasSingingObservation(m_FastLoopAcceptedAudioFacts?["timeOrder"])) return;
        string clip = (string)m_FastLoopAcceptedAudioFacts["clipRef"];
        m_FastLoopRecentSingingObservations.RemoveAll(v => (string)v["clipRef"] == clip);
        m_FastLoopRecentSingingObservations.Add(new JObject {
            ["clipRef"] = clip, ["userTurn"] = m_SingingGoalTurn,
            ["captureSessionSerial"] = m_FastLoopInputFactsCapture,
            ["timeOrder"] = m_FastLoopAcceptedAudioFacts["timeOrder"].DeepClone() });
        while (m_FastLoopRecentSingingObservations.Count > 2) m_FastLoopRecentSingingObservations.RemoveAt(0);
    }

    private void PruneFastLoopSingingObservations(SenseVoiceSpeechToText sense)
    {
        if (!ReferenceEquals(m_FastLoopMaterialProvider, sense))
        { m_FastLoopRecentSingingObservations.Clear(); m_FastLoopMaterialProvider = sense; }
        // The existing 16-user-turn session window is the lifetime boundary.
        // Clearing conversation history cannot attach prior-session evidence to new words.
        m_FastLoopRecentSingingObservations.RemoveAll(v => (int)v["userTurn"] != m_SingingGoalTurn &&
            !m_SingingUserInputs.Any(u => u.turn == (int)v["userTurn"]));
    }

    // Snapshot existing local state only. No health request, warmup, retention,
    // permission grant, range selection or tool invocation is performed here.
    private void EnrichFastLoopContext(JObject facts)
    {
        if (facts == null) return;
        m_SkillRouteStates.TryGetValue("singing", out var singing);
        float now = Time.realtimeSinceStartup;
        facts["voiceCapabilities"] = new JObject {
            ["source"] = "registered-runtime-components",
            ["voiceMode"] = m_IsVoiceMode, ["ttsBound"] = m_ChatSettings?.m_TextToSpeech != null,
            ["audioSourceBound"] = m_AudioSource != null, ["speechPlaying"] = IsAISpeaking || IsVoiceOutputPlaying,
            ["singing"] = new JObject {
                ["enabled"] = m_EnableAutonomousHumBack,
                ["access"] = (singing?.Access ?? SkillAccess.Available).ToString(),
                ["detailedSkillLoaded"] = singing?.ActiveThisRound == true,
                ["svcEnabled"] = m_EnableNeuralHumSVC, ["svsEnabled"] = m_EnableSingingVoiceSynthesis,
                ["paths"] = new JObject {
                    ["recordingSingBack"] = new JObject { ["registered"] = m_EnableNeuralHumSVC,
                        ["description"] = "Convert an existing reviewed recording into the character voice; does not require SVS.",
                        ["handoffRoute"] = "recording_singing" },
                    ["scoreSynthesis"] = new JObject { ["registered"] = m_EnableSingingVoiceSynthesis,
                        ["description"] = "Generate singing from a score through a separate SVS service.",
                        ["handoffRoute"] = "legacy" }
                },
                ["requestSubmitted"] = m_SingingRequestSubmittedSinceUserTurn,
                ["workActive"] = IsSingingRuntimeActivityActive(),
                ["serviceObservation"] = new JObject {
                    ["reachable"] = m_ObservedSvcReachable, ["healthy"] = m_ObservedSvcHealthy,
                    ["workerReady"] = m_ObservedSvcWorkerReady,
                    ["healthAgeSeconds"] = m_ObservedSvcHealthAt < 0f ? (float?)null : now - m_ObservedSvcHealthAt,
                    ["fresh"] = m_ObservedSvcHealthAt >= 0f && now - m_ObservedSvcHealthAt < 90f,
                    ["scope"] = "Last observed response only, not a new probe. null means unobserved."
                }
            },
            ["scope"] = "Registered/enabled/loaded/reachable/submitted/played are distinct. " +
                "Ordinary speech TTS is not singing. A pending source review is not a missing recording or a service failure."
        };
        var skills = new JArray();
        foreach (var definition in s_SkillRouteDefinitions.Take(8))
        {
            m_SkillRouteStates.TryGetValue(definition.Name, out var state);
            skills.Add(new JObject { ["name"] = definition.Name, ["registered"] = true,
                ["description"] = FastLoopPrompt.ObservationText(definition.AutonomousDescription, 120),
                ["access"] = (state?.Access ?? SkillAccess.Available).ToString(),
                ["detailedRulesLoaded"] = state?.ActiveThisRound == true,
                ["loadedInRound"] = m_ActiveSkillsThisRound.Contains(definition.Name),
                ["handoffRoute"] = "legacy" });
        }
        facts["skills"] = skills;
        facts["work"] = new JObject {
            ["epoch"] = m_WorkEpoch, ["open"] = m_WorkReviewOpen, ["status"] = m_WorkStatus,
            ["intent"] = FastLoopPrompt.ObservationText(m_WorkIntent, 240),
            ["toolInFlight"] = HasWorkToolInFlight(),
            ["singingGoal"] = FastLoopSingingGoal(m_SingingGoal),
            ["activeSingingGoal"] = FastLoopSingingGoal(m_ActiveSingingGoal),
            ["previousSingingGoal"] = FastLoopSingingGoal(m_PreviousSingingGoal),
            ["lastSingingReviewFailure"] = BuildSingingReviewFailureFact(),
            ["singingRejections"] = FastLoopSingingRejections(),
            ["lastSingingPlayback"] = m_LastSingingGoalPlayback == null ? null : new JObject {
                ["turn"] = m_LastSingingGoalPlayback.turn, ["completed"] = m_LastSingingGoalPlayback.completed,
                ["userConfirmed"] = m_LastSingingGoalPlayback.userConfirmed,
                ["targetMatches"] = m_LastSingingGoalPlayback.targetMatches,
                ["materials"] = new JArray(m_LastSingingGoalPlayback.materials.Select(v => new JObject {
                    ["clipRef"] = v.clip, ["revision"] = v.revision,
                    ["sourceStartSeconds"] = v.start, ["sourceEndSeconds"] = v.end })),
                ["scope"] = m_LastSingingGoalPlayback.completed
                    ? "Completed historical playback. Completion proves audio was played, not that the requested verse was selected correctly."
                    : "Attempt source snapshot only. Incomplete does not establish that any audio was played; it cannot replace lastCompletedSingingPlayback."
            },
            ["lastCompletedSingingPlayback"] = new JObject {
                ["sequence"] = m_RecordingPlaybackSequence,
                ["materials"] = new JArray(m_LastCompletedBoundarySources.Select(v => new JObject {
                    ["clipRef"] = v.Clip, ["revision"] = v.Revision,
                    ["sourceStartSeconds"] = v.Start, ["sourceEndSeconds"] = v.End })),
                ["scope"] = "Last completed sequence only; an interrupted attempt does not overwrite it. Historical source ranges, not permission or current selection."
            },
            ["scope"] = "A role proposal and a completed utterance are not execution or content-verification receipts."
        };
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        facts["recordingMaterials"] = FastLoopRecordingMaterials(sense);
        facts["inputObservation"] = FastLoopInputObservation(sense);
        facts["singingSession"] = BuildSingingSessionFacts();
        facts["singingSessionMode"] = "independent-event";
        facts["listeningPreparation"] = BuildIncrementalListeningFacts();
        var users = new JArray(m_SingingUserInputs.Select(v => new JObject { ["turn"] = v.turn, ["text"] = v.text }));
        facts["recentDialogue"] = FastLoopPrompt.Dialogue(users, new JArray(m_CompletedRoleReplies), m_SingingGoalTurn);
        facts["spatialObjectMemory"] = BuildSpatialObjectMemory();
        facts["previousPlanReferent"] = PreviousActorPlanFacts();
        facts["recentActionOutcomes"] = BuildRecentActionOutcomes();
        // Failure details may contain old draft text. Keep them bounded and
        // explicitly separate from the user's current utterance and intention.
        if (facts["executionFeedback"]?.Type == JTokenType.String)
            facts["executionFeedback"] = FastLoopPrompt.ObservationText((string)facts["executionFeedback"], 420);
    }

    private static JObject FastLoopSingingGoal(SingingGoal goal) => goal == null ? null : new JObject {
        ["turn"] = goal.turn, ["version"] = goal.version, ["origin"] = goal.origin,
        ["intent"] = goal.intent, ["review"] = goal.review, ["outcome"] = goal.outcome,
        ["targetReviewed"] = goal.targetReviewed,
        ["proposedReferences"] = FastLoopPrompt.ObservationText(goal.refs, 180),
        ["requestQuote"] = FastLoopPrompt.ObservationText(goal.requestQuote, 240),
        ["expected"] = FastLoopPrompt.ObservationText(goal.expected, 180)
    };

    private JArray FastLoopSingingRejections() => new JArray(BuildSingingRejectionHistoryFacts().OfType<JObject>().Reverse().Take(4).Reverse()
        .Select(v => new JObject {
            ["id"] = v["id"]?.DeepClone(), ["userTurn"] = v["userTurn"]?.DeepClone(),
            ["expected"] = v["expected"]?.DeepClone(), ["validated"] = v["validated"]?.DeepClone(),
            ["resolution"] = v["resolution"]?.DeepClone(), ["constraint"] = v["constraint"]?.DeepClone(),
            ["scope"] = "Historical independent review, not a new user instruction. A rejected candidate is not a missing recording; source selection and user intent still need current review."
        }));

    private JObject FastLoopInputObservation(SenseVoiceSpeechToText sense)
    {
        bool bound = FastLoopInputFactsCurrent;
        bool audio = bound && m_FastLoopAcceptedAudioFacts != null;
        return new JObject {
            ["acceptedInputBound"] = bound, ["generation"] = m_FormalResponseGeneration,
            ["source"] = audio ? "accepted-final-audio" : bound ? "accepted-text" : "caller-input",
            ["acoustic"] = !audio ? null : m_FastLoopAcceptedAudioFacts.DeepClone(),
            ["scope"] = "Acoustics are limited measurements of the accepted recording, not user instructions, " +
                "source approval, a singing request or evidence that the role acted. Current_user keeps the complete accepted transcript including its ending."
        };
    }

    private JObject FastLoopRecordingMaterials(SenseVoiceSpeechToText sense)
    {
        var records = new List<JObject>();
        PruneFastLoopSingingObservations(sense);
        if (sense != null)
        {
            foreach (var item in sense.DescribePracticePhrases())
                records.Add(new JObject { ["clipRef"] = item.ClipRef, ["recordingSequence"] = item.RecordingSequence,
                    ["revision"] = item.Revision, ["sourceStatus"] = item.PendingConfirmation ? "pending" : "confirmed_user",
                    ["playbackStatus"] = "ready", ["rawSeconds"] = item.RawSeconds,
                    ["hasCleanRange"] = HasRecoverableRecordingRange(item.RawSeconds, item.CleanStartSeconds, item.CleanEndSeconds),
                    ["hasExpandedRange"] = HasRecoverableRecordingRange(item.RawSeconds, item.ExpandedStartSeconds, item.ExpandedEndSeconds),
                    ["transcriptExcerpt"] = FastLoopPrompt.ObservationText(item.RecordingTranscript, 150),
                    ["transcriptCharacters"] = (item.RecordingTranscript ?? "").Length,
                    ["transcriptTruncated"] = (item.RecordingTranscript ?? "").Length > 150 });
            foreach (var item in sense.DescribeQuarantinedSingingCandidates())
                records.Add(new JObject { ["clipRef"] = item.ClipRef, ["recordingSequence"] = item.RecordingSequence,
                    ["revision"] = "pending", ["sourceStatus"] = item.SourceStatus,
                    ["playbackStatus"] = item.PlaybackStatus, ["rawSeconds"] = item.RawSeconds,
                    ["hasCleanRange"] = HasRecoverableRecordingRange(item.RawSeconds, item.CleanStartSeconds, item.CleanEndSeconds),
                    ["hasExpandedRange"] = HasRecoverableRecordingRange(item.RawSeconds, item.ExpandedStartSeconds, item.ExpandedEndSeconds),
                    ["transcriptExcerpt"] = FastLoopPrompt.ObservationText(item.WholeTurnText, 150),
                    ["transcriptCharacters"] = (item.WholeTurnText ?? "").Length,
                    ["transcriptTruncated"] = (item.WholeTurnText ?? "").Length > 150 });
        }
        m_FastLoopRecentSingingObservations.RemoveAll(v => !records.Any(r => (string)r["clipRef"] == (string)v["clipRef"]));
        foreach (var record in records)
        {
            var observation = m_FastLoopRecentSingingObservations.LastOrDefault(v => (string)v["clipRef"] == (string)record["clipRef"]);
            if (observation == null) continue;
            record["observedUserTurn"] = observation["userTurn"].DeepClone();
            record["observedCaptureSessionSerial"] = observation["captureSessionSerial"].DeepClone();
            record["timeOrder"] = observation["timeOrder"].DeepClone();
        }
        var refs = new[] { m_SingingGoal, m_ActiveSingingGoal }.Where(g => g != null && g.intent != "observe" &&
            g.review != "cancelled" && g.outcome != "cancelled" && g.outcome != "user_confirmed")
            .SelectMany(g => (g.refs ?? "").Split(',')).Select(r => sense?.CanonicalSingingClipReference(r.Trim()))
            .Where(r => !string.IsNullOrEmpty(r));
        var reviewRefs = BuildSingingRejectionHistoryFacts().OfType<JObject>().Reverse()
            .Where(v => v["resolution"] == null || v["resolution"].Type == JTokenType.Null)
            .SelectMany(v => ((string)(v["expected"] as JObject)?["refs"] ?? "").Split(','))
            .Select(r => sense?.CanonicalSingingClipReference(r.Trim())).Where(r => !string.IsNullOrEmpty(r));
        var result = FastLoopPrompt.RecordingMaterials(new JArray(records), refs, sense != null, reviewRefs);
        result["fullEvidenceRoute"] = "recording_singing";
        result["projectionScope"] = "Bounded inventory index, not a verdict about which recordings contain singing. The dedicated recording decision reads complete retained transcripts/ranges; an omitted or shortened row is not missing audio.";
        // The current timeline appears once. Older relevant timelines remain self-contained.
        if (FastLoopInputFactsCurrent && m_FastLoopAcceptedAudioFacts != null)
            foreach (var record in ((JArray)result["recent"]).OfType<JObject>())
                if ((string)record["clipRef"] == (string)m_FastLoopAcceptedAudioFacts["clipRef"] && record["timeOrder"] is JObject)
                { record.Remove("timeOrder"); record["timeOrderRef"] = "inputObservation.acoustic.timeOrder"; }
        return result;
    }
}
