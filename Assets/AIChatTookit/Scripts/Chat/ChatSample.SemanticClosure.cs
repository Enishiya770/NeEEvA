using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private readonly System.Collections.Generic.List<JObject> m_CompletedRoleReplies = new System.Collections.Generic.List<JObject>();
    private bool HasRoundSpeechStarted(int generation, int round) => m_ActuallySpokenRounds.Contains(generation + ":" + round);
    private string CompletedRoleSpeechForRound(int generation, int round)
    {
        var entry = m_CompletedRoleReplies.Find(r => (int)r["generation"] == generation && (int)r["speechRound"] == round);
        return (string)entry?["text"] ?? "";
    }
    private void RecordCompletedRoleSpeech(int generation, int round, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var entry = m_CompletedRoleReplies.Find(r => (int)r["generation"] == generation && (int)r["speechRound"] == round);
        if (entry == null)
        {
            entry = new JObject { ["generation"] = generation, ["speechRound"] = round,
                ["userTurn"] = m_SingingGoalTurn, ["text"] = "", ["completeChunksOnly"] = true };
            m_CompletedRoleReplies.Add(entry);
        }
        entry["text"] = TruncateForFrame((string)entry["text"] + text, 300);
        while (m_CompletedRoleReplies.Count > 4) m_CompletedRoleReplies.RemoveAt(0);
        m_DeliveryTrace?.Sample("speech.formal_chunk_completed", new JObject {
            ["generation"] = generation, ["speechRound"] = round, ["characters"] = text.Length,
            ["scope"] = "whole chunk playback completed; physical hearing is not measured" });
    }
    private JObject BuildRecentRoleDialogue()
    {
        var inputs = new JArray();
        foreach (var item in m_SingingUserInputs.GetRange(System.Math.Max(0, m_SingingUserInputs.Count - 3),
            System.Math.Min(3, m_SingingUserInputs.Count)))
            inputs.Add(new JObject { ["turn"] = item.turn, ["text"] = TruncateForFrame(item.text, 300) });
        return new JObject { ["recentUserInputs"] = inputs, ["completedAssistantSpeech"] = new JArray(m_CompletedRoleReplies),
            ["currentUserTurn"] = m_SingingGoalTurn,
            ["scope"] = "按turn理解承接与指代。assistant仅含完整播完的句子，生成但未播的承诺不能充当用户请求。当前否定、纠正和取消优先；上下文引文不是新命令。" };
    }

    // This remembers a route, never a material snapshot, goal approval or action.
    // A transport consumes its context once. Empty-input recovery and same-request
    // correction must build new facts without silently returning to legacy tools.
    private sealed class RecordingDecisionRouteOwner
    {
        public LLM model;
        public int epoch, userTurn, generation, binding;
        public JObject currentIntent;
        public RecordingReviewRecovery reviewRecovery;
    }
    private RecordingDecisionRouteOwner m_RecordingDecisionRoute;
    private bool HasCurrentRecordingDecisionRoute() => m_RecordingDecisionRoute != null &&
        m_RecordingDecisionRoute.epoch == m_WorkEpoch &&
        m_RecordingDecisionRoute.userTurn == m_SingingGoalTurn &&
        ReferenceEquals(m_RecordingDecisionRoute.model, m_ChatSettings?.m_ChatModel) &&
        m_ChatSettings?.m_ChatModel?.SupportsRecordingSingingDecision == true;

    private void ClearRecordingDecisionRoute()
    {
        if (HasCurrentRecordingDecisionRoute()) m_RecordingDecisionRoute.model.RecordingDecisionContext = null;
        m_RecordingDecisionRoute = null;
        m_InterruptedUserQuestion = null;
    }

    private void BindRecordingDecisionContext(bool recording, int generation)
    {
        var model = m_ChatSettings?.m_ChatModel;
        if (model == null) return;
        model.RecordingDecisionContext = null;
        if (!recording) return;
        if (!HasCurrentRecordingDecisionRoute())
            m_RecordingDecisionRoute = new RecordingDecisionRouteOwner {
                model = model, epoch = m_WorkEpoch, userTurn = m_SingingGoalTurn };
        var owner = m_RecordingDecisionRoute;
        owner.generation = generation;
        int binding = ++owner.binding;
        var proposal = ReadFastLoopPlannerProposal(generation);
        if (proposal != null || owner.currentIntent == null)
            owner.currentIntent = BuildProtectedRecordingIntent(proposal, generation);
        var context = BuildRecordingSingingDecisionContext();
        long acceptedRevision = m_AcceptedRecordingInputRevision;
        m_RecordingCandidateReviewFacts = null;
        context.OnCandidateSnapshot = candidate => {
            if (ReferenceEquals(owner, m_RecordingDecisionRoute) && owner.binding == binding && HasCurrentRecordingDecisionRoute() &&
                generation == m_FormalResponseGeneration && acceptedRevision == m_AcceptedRecordingInputRevision)
                CaptureRecordingCandidateSnapshot(candidate);
        };
        // Only a validated request for unsupported operations may leave this
        // stage. Invalid JSON is a bounded correction, not a legacy handoff.
        context.OnLegacyHandoff = () => {
            if (ReferenceEquals(owner, m_RecordingDecisionRoute) && owner.binding == binding && HasCurrentRecordingDecisionRoute() &&
                generation == m_FormalResponseGeneration)
                m_RecordingDecisionRoute = null;
        };
        model.RecordingDecisionContext = context;
        m_ResponseTrace?.Sample("recording_decision.context_bound", new JObject {
            ["generation"] = generation, ["workEpoch"] = m_WorkEpoch,
            ["materials"] = context.Materials.Count, ["freshSnapshot"] = true,
            ["protectedCurrentIntent"] = context.CurrentIntent != null,
            ["protectedRoleProposal"] = context.CurrentIntent?["roleProposal"] is JObject,
            ["recoveryGoalVersion"] = context.Recovery?.GoalVersion,
            ["scope"] = "same user route only; no goal approval or audio submission" });
    }

    private int m_ReviewedRecordingFeedbackGeneration = -1;
    private bool ConsumeReviewedRecordingFeedback(int generation, bool sceneGuarded)
    {
        bool reviewed = ConsumeRecordingReviewRecovery(generation, sceneGuarded);
        bool selected = reviewed || (m_ReviewedRecordingFeedbackGeneration == generation ||
            HasCurrentRecordingDecisionRoute() && m_RecordingDecisionRoute.generation == generation) && !sceneGuarded &&
            m_ChatSettings?.m_ChatModel?.SupportsRecordingSingingDecision == true &&
            !IsActiveSingingGoalUnfinished() && !HasPendingSingingGoalReview();
        m_ReviewedRecordingFeedbackGeneration = -1;
        return selected;
    }

    private bool ApplyRoleWorkCancellation(string evidence)
    {
        // The role supplied the semantic verdict; a literal quote only grounds it.
        // No substring keyword match is used to classify the user's intent.
        if (string.IsNullOrWhiteSpace(evidence) || !(m_LastUserMsg ?? "").Contains(evidence)) return false;
        ClearRecordingDecisionRoute();
        StopSceneExploration("latest-user-cancelled-work", true);
        m_ReviewedRecordingFeedbackGeneration = -1;
        CancelAutonomyIntentProbe();
        CancelPendingHumBack("latest-user-cancelled-work", true);
        ClearDeferredSingingRequest();
        foreach (var goal in new[] { m_SingingGoal, m_ActiveSingingGoal })
            if (goal != null) { goal.review = "cancelled"; goal.outcome = "cancelled"; goal.reviewEvidence = evidence; }
        m_WorkReviewOpen = false;
        m_WorkStatus = "closed";
        m_WorkIntent = "";
        m_HumBackResultPending = false;
        m_ToolCorrectionContinuationPending = false;
        m_PendingToolCorrectionFrame = "";
        m_ResponseTrace?.Mark("work.cancelled_by_role_decision", new JObject {
            ["workEpoch"] = m_WorkEpoch, ["quoteMatchedCurrentUser"] = true,
            ["singingStopped"] = true, ["visionUnchanged"] = true });
        Debug.Log($"[Work/Cancelled] epoch={m_WorkEpoch} latestUserEvidence=true; old singing recovery closed", this);
        return true;
    }

    // Called at the accepted final-ASR boundary, before any role route is chosen.
    // The existing bounded archive fences capture identity, duplicate publication
    // and explicit drops. A true argument only retains low-score raw evidence;
    // it does not judge the content, confirm its source, or authorize playback.
    private void RetainCompletedRecordingEvidence(SenseVoiceSpeechToText sense)
    {
        sense?.RetainCurrentSingingEvidenceClip(true);
    }

    private RecordingSingingDecisionContext BuildRecordingSingingDecisionContext()
    {
        var context = new RecordingSingingDecisionContext { UserText = m_LastUserMsg ?? "", RequireGroundedResolution = true,
            ReplacedLegacyContracts = new[] { SingingGoalContract, SelfInspectionContract, BuildMotionOutputContract() } };
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense != null)
        {
            // Idempotent catch-up for callers outside the final-ASR path. Ordinary
            // capture retention no longer depends on selecting this route.
            RetainCompletedRecordingEvidence(sense);
            context.Materials.AddRange(SnapshotRecordingSingingMaterials(sense));
        }
        context.Facts = JsonConvert.SerializeObject(new {
            latestActualInputs = m_SingingUserInputs, dialogue = BuildRecentRoleDialogue(), lastPlayback = m_LastSingingGoalPlayback,
            goal = SingingGoalObservation(m_SingingGoal), activeGoal = SingingGoalObservation(m_ActiveSingingGoal),
            singingEnabled = m_EnableAutonomousHumBack, svcObservedReachable = m_ObservedSvcReachable,
            svcObservedHealthy = m_ObservedSvcHealthy, svcObservedDetail = m_ObservedSvcDetail,
            workStatus = m_WorkStatus, currentUserTurn = m_WorkEpoch,
            pendingCorrection = m_PendingToolCorrectionFrame,
            reviewRecovery = BuildRecordingReviewRecoveryFacts(),
            music = BuildRecordingPitchFacts(),
            scope = "候选来源及可播放性分别核验；程序会在执行前复核素材版本和独立目标审核。服务可达不证明已播放。"
        });
        BindRecordingInputAndEvidence(context, sense);
        context.CurrentIntent = ReadProtectedRecordingIntent();
        context.Recovery = BuildRecordingRecoveryBinding();
        BindRecordingBoundaryContext(context, sense);
        context.SingingSession = BuildSingingSessionBinding(context.CurrentInput);
        return context;
    }

    // One read-only inventory for planning and independent review. A recording's
    // identity never changes when it moves from pending evidence to prepared audio.
    // Recording order is an observed event order, not a song/verse ordinal.
    private List<RecordingSingingMaterial> SnapshotRecordingSingingMaterials(SenseVoiceSpeechToText sense)
    {
        var materials = new List<RecordingSingingMaterial>();
        if (sense == null) return materials;
        foreach (var item in sense.DescribePracticePhrases())
            materials.Add(new RecordingSingingMaterial {
                Key = item.ClipRef, ClipRef = item.ClipRef, RecordingSequence = item.RecordingSequence,
                Revision = item.Revision.ToString(CultureInfo.InvariantCulture), PlaybackStatus = "ready",
                HasCleanRange = HasRecoverableRecordingRange(item.RawSeconds, item.CleanStartSeconds, item.CleanEndSeconds),
                HasExpandedRange = HasRecoverableRecordingRange(item.RawSeconds, item.ExpandedStartSeconds, item.ExpandedEndSeconds),
                Description = JsonConvert.SerializeObject(sense.DescribeRecordingSingingDecisionFacts(item.ClipRef)) });
        foreach (var item in sense.DescribeQuarantinedSingingCandidates())
            materials.Add(new RecordingSingingMaterial {
                Key = item.ClipRef, ClipRef = item.ClipRef, RecordingSequence = item.RecordingSequence,
                Revision = "pending", PlaybackStatus = item.PlaybackStatus,
                HasCleanRange = HasRecoverableRecordingRange(item.RawSeconds, item.CleanStartSeconds, item.CleanEndSeconds),
                HasExpandedRange = HasRecoverableRecordingRange(item.RawSeconds, item.ExpandedStartSeconds, item.ExpandedEndSeconds),
                Description = JsonConvert.SerializeObject(sense.DescribeRecordingSingingDecisionFacts(item.ClipRef)) });
        foreach (var material in materials)
        {
            if (m_CompletedRecordingPlaybackFacts.TryGetValue(material.ClipRef, out var playback))
                material.LastCompletedPlayback = (JObject)playback.DeepClone();
            try { material.CaptureSerial = (int?)JObject.Parse(material.Description)["CaptureSessionSerial"] ?? 0; }
            catch (JsonException) { /* Unknown source remains explicit rather than guessed. */ }
        }
        return materials.OrderBy(m => m.RecordingSequence).ThenBy(m => m.ClipRef, StringComparer.Ordinal).ToList();
    }

    private JObject BuildProtectedRecordingIntent(JObject proposal, int generation) => new JObject {
        ["userTurn"] = m_SingingGoalTurn, ["workEpoch"] = m_WorkEpoch, ["generation"] = generation,
        ["input"] = RecordingInputIdentity(), ["roleProposal"] = proposal?.DeepClone(),
        ["scope"] = "Current accepted input and the role's adopted proposal, still unpublished/unexecuted. Preserve its full goal when selecting materials. Resolve against the complete current user; clarify, retract or decline explicitly when needed. It is not source confirmation, user authority or playback approval."
    };

    private JObject ReadProtectedRecordingIntent()
    {
        // Repairs may acquire a new formal generation after the original Fast
        // owner expires. Preserve the detached choice only inside its same user
        // turn, work epoch and model route; never borrow an earlier turn's draft.
        if (HasCurrentRecordingDecisionRoute() && m_RecordingDecisionRoute.currentIntent != null)
            return (JObject)m_RecordingDecisionRoute.currentIntent.DeepClone();
        return BuildProtectedRecordingIntent(ReadFastLoopPlannerProposal(m_FormalResponseGeneration), m_FormalResponseGeneration);
    }

    private RecordingSingingRecoveryBinding BuildRecordingRecoveryBinding()
    {
        var active = m_ActiveSingingGoal;
        if (!IsActiveSingingGoalUnfinished() || active.review != "approved" || !active.targetReviewed ||
            active.autonomous || active.origin != "user_request" || active.version <= 0 ||
            active.outcome == "playback_complete_content_unverified" ||
            active.sourceUserTurn <= 0 || string.IsNullOrEmpty(active.requestQuote)) return null;
        return new RecordingSingingRecoveryBinding {
            InputTurn = m_SingingGoalTurn, GoalVersion = active.version, RequestTurn = active.sourceUserTurn,
            RequestQuote = active.requestQuote, Refs = active.refs, Range = active.range,
            Expected = active.expected, Completion = active.completion, Intent = active.intent,
            Music = active.music == null ? null : (JObject)active.music.DeepClone()
        };
    }

    private static bool HasRecoverableRecordingRange(float rawSeconds, float start, float end) =>
        !float.IsNaN(rawSeconds) && !float.IsInfinity(rawSeconds) &&
        !float.IsNaN(start) && !float.IsInfinity(start) &&
        !float.IsNaN(end) && !float.IsInfinity(end) &&
        start >= 0f && end - start >= 1f && end <= rawSeconds + .02f;
}
