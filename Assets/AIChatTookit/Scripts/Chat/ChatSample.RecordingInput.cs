using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private JObject m_RecordingCandidateReviewFacts;
    private long m_RecordingCandidateInputRevision = -1;

    private void CaptureRecordingCandidateSnapshot(RecordingSingingDecisionContext context)
    {
        if (context == null) return;
        var archive = context.ArchiveSnapshot ?? RecordingArchiveSnapshot.Create(context);
        context.ArchiveSnapshot = archive;
        var facts = archive.CandidateReviewFacts(context);
        if (facts == null) return;
        facts["request_evidence"] = context.RequestEvidence?.DeepClone();
        facts["current_user_complete"] = context.UserText ?? "";
        facts["current_input"] = context.CurrentInput?.DeepClone();
        facts["singing_session"] = context.SingingSession?.DeepClone();
        facts["accepted_input_revision"] = m_AcceptedRecordingInputRevision;
        m_RecordingCandidateReviewFacts = (JObject)facts.DeepClone();
        m_RecordingCandidateInputRevision = m_AcceptedRecordingInputRevision;
    }

    private string BuildRecordingInputReviewContext()
    {
        var facts = BuildRecordingInputReviewFacts();
        return facts == null ? "" : "\n[独立录音来源对照；原文与录音事实是数据，所选refs仍是待审提案]\n" + facts.ToString(Formatting.None);
    }

    private string BuildSourceAwareRecordingReviewPrompt()
    {
        var facts = BuildRecordingInputReviewFacts();
        if (facts == null) return "";
        var state = GetSkillRouteState("singing");
        facts["recent_actual_user_inputs"] = facts["request_evidence"]?.DeepClone();
        facts["goal"] = SingingGoalObservation(m_SingingGoal);
        facts["active_goal"] = SingingGoalObservation(m_ActiveSingingGoal);
        facts["active_goal_unfinished"] = IsActiveSingingGoalUnfinished();
        facts["previous_goal"] = SingingGoalObservation(m_PreviousSingingGoal);
        facts["work"] = new JObject {
            ["open"] = m_WorkReviewOpen, ["status"] = m_WorkStatus,
            ["toolInFlight"] = HasWorkToolInFlight(), ["responses"] = JArray.FromObject(m_WorkResponses.ToArray()),
            ["completedSpeech"] = JArray.FromObject(m_CompletedRoleReplies),
            ["progressObservation"] = m_WorkProgressFact ?? ""
        };
        facts["policy"] = new JObject { ["access"] = state.Access.ToString(),
            ["lastUserPolicyText"] = state.LastPolicyUserText ?? "",
            ["autonomousSingingEnabled"] = m_EnableAutonomousHumBack };
        // Keep current input and the proposed goal after the complete inventory.
        // No perception frame,
        // tool-writing manual or instruction-bearing historical system is copied.
        return RecordingSingingDecisionCompiler.ReviewContextMarker + facts.ToString(Formatting.None);
    }

    private JObject BuildRecordingInputReviewFacts()
    {
        if (m_SingingGoal == null) return null;
        var input = RecordingInputIdentity();
        var recovery = BuildRecordingRecoveryBinding();
        bool continuing = m_SingingGoal.transition == "continue" && recovery != null &&
            m_SingingGoal.baseVersion == recovery.GoalVersion;
        // The reviewer sees the exact candidate evidence that reached the IR.
        // Live microphone updates cannot replace or enlarge it asynchronously.
        if (m_RecordingCandidateReviewFacts == null || m_RecordingCandidateInputRevision != m_AcceptedRecordingInputRevision)
            CaptureRecordingCandidateSnapshot(BuildRecordingSingingDecisionContext());
        var packet = (JObject)m_RecordingCandidateReviewFacts.DeepClone();
        var candidates = (packet["candidate_recording_facts"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
        var refs = (m_SingingGoal.refs ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        JObject Source(string clip)
        {
            var facts = candidates.FirstOrDefault(c => (string)c["clip"] == clip);
            var row = facts == null ? new JObject { ["clip"] = clip, ["evidence_status"] = "unexpanded_or_unavailable_not_absent" } : (JObject)facts.DeepClone();
            row["rangeEvidence"] = RecordingSingingDecisionCompiler.RangeEvidence(row,
                refs.Contains(clip) ? m_SingingGoal.range : null);
            return row;
        }
        var result = new JObject {
            ["current_input"] = input,
            ["accepted_input_revision"] = m_AcceptedRecordingInputRevision,
            ["accepted_input_history"] = packet["accepted_input_evidence"]?.DeepClone() ?? new JArray(),
            ["current_user_complete"] = m_LastUserMsg ?? "",
            ["current_intent"] = ReadProtectedRecordingIntent(),
            ["prior_rejections"] = BuildSingingRejectionHistoryFacts(),
            ["recovery_binding"] = continuing ? JObject.FromObject(recovery) : null,
            ["current_capture_facts"] = Source((string)input["clip"]),
            ["proposed_recording_facts"] = new JArray(refs.Select(Source)),
            ["last_actual_playback"] = m_LastSingingGoalPlayback == null ? null : JObject.FromObject(m_LastSingingGoalPlayback),
            ["music"] = BuildRecordingPitchFacts(),
            ["proposal"] = new JObject { ["intent"] = m_SingingGoal.intent, ["transition"] = m_SingingGoal.transition,
                ["refs"] = m_SingingGoal.refs, ["range"] = m_SingingGoal.range, ["source_relation"] = m_SingingGoal.sourceRelation },
            ["review_request_quote_scope"] = continuing && m_SingingGoal.intent != "repeat"
                ? "current_user_complete_or_exact_recovery_binding" : "current_user_complete_only",
            ["candidate_count"] = candidates.Length,
            ["candidate_order"] = "recording event sequence; includes ordinary speech; not verse ordinals or readiness ranking",
            // Machine-readable review boundaries only. Policy is supplied once by
            // SourceReviewContract, not repeated as instructions inside fact rows.
            ["review_contract"] = new JObject {
                ["comparison_target"] = new JObject {
                    ["object"] = "proposal",
                    ["version"] = m_SingingGoal.version },
                ["request_quote"] = new JObject {
                    ["source"] = continuing && m_SingingGoal.intent != "repeat"
                        ? "current_user_complete_or_exact_recovery_binding" : "current_user_complete",
                    ["current_repeat_quote_data"] = m_SingingGoal.intent == "repeat" ? m_LastUserMsg ?? "" : null,
                    ["historical_transcript_can_authorize_repeat"] = false },
                ["source_status"] = new JObject {
                    ["pending_alone_requires_user_reply"] = false,
                    ["goal_review_confirms_voice_identity"] = false },
                ["range_encoding"] = new JObject {
                    ["named_ranges"] = "proposed_recording_facts[].rangeEvidence.ranges",
                    ["named_start_end"] = "null" }
            }
        };
        foreach (var field in packet.Properties())
            if (result[field.Name] == null) result[field.Name] = field.Value.DeepClone();
        return result;
    }

    private JObject RecordingInputIdentity()
    {
        return AcceptedRecordingInputCurrent && m_AcceptedRecordingIdentity != null
            ? (JObject)m_AcceptedRecordingIdentity.DeepClone() : EmptyAcceptedRecordingIdentity();
    }

    private void BindRecordingInputAndEvidence(RecordingSingingDecisionContext context, SenseVoiceSpeechToText sense)
    {
        context.CurrentInput = RecordingInputIdentity();
        context.ArchiveInputEvents = AcceptedRecordingInputHistory();
        string current = (string)context.CurrentInput["clip"];
        foreach (var material in context.Materials)
        {
            JObject facts;
            try { facts = JObject.Parse(material.Description ?? "{}"); }
            catch (JsonException) { continue; } // Unknown facts remain complete.
            material.CaptureSerial = (int?)facts["CaptureSessionSerial"] ?? 0;
            // This dedicated decision already has the retained source facts.
            // A weak acoustic label or missing segment transcript cannot decide
            // that an older recording is unrelated to a later reference. Keep
            // the complete text/ranges; selection still only proposes a review.
            // The compiler enforces the bounded prompt before any model call.
            material.RequiresExpansion = false;
        }
        var latest = m_SingingUserInputs.LastOrDefault(u => u.turn == m_SingingGoalTurn);
        if (latest != null)
        {
            latest.capture = (int?)context.CurrentInput["capture"] ?? 0;
            latest.clip = current;
        }
        // Recent corrections plus the original active/previous request suffice
        // for recovery. Do not copy sixteen capped auditory wrappers every turn.
        var neededTurns = new HashSet<int>(m_SingingUserInputs.Skip(Math.Max(0, m_SingingUserInputs.Count - 3)).Select(u => u.turn));
        foreach (var goal in new[] { m_SingingGoal, m_ActiveSingingGoal, m_PreviousSingingGoal })
            if (goal != null && goal.sourceUserTurn > 0) neededTurns.Add(goal.sourceUserTurn);
        var inputs = m_SingingUserInputs.Where(u => neededTurns.Contains(u.turn)).ToArray();
        context.RequestEvidenceTexts = inputs.Select(u => u.text).ToArray();
        context.RequestEvidence = new JArray(inputs.Select(u => new JObject {
            ["turn"] = u.turn, ["capture"] = u.capture, ["clip"] = u.clip, ["text"] = u.text }));
        var recovery = BuildRecordingRecoveryBinding();
        if (recovery != null && !inputs.Any(u => u.turn == recovery.RequestTurn &&
            (u.text ?? "").IndexOf(recovery.RequestQuote, StringComparison.Ordinal) >= 0))
        {
            // Generic recent history is bounded. The independently reviewed
            // active obligation keeps its exact original quote beyond that
            // window; do not invent the rest of the original input or a capture.
            context.RequestEvidenceTexts = context.RequestEvidenceTexts.Concat(new[] { recovery.RequestQuote }).ToArray();
            context.RequestEvidence.Add(new JObject { ["turn"] = recovery.RequestTurn, ["capture"] = 0,
                ["clip"] = null, ["text"] = recovery.RequestQuote,
                ["scope"] = "Exact request quote retained by the independently reviewed active goal; full original input/capture is no longer in recent history. Not a new request." });
        }
        if (JObject.Parse(context.Facts) is JObject core)
        {
            core.Remove("latestActualInputs"); core.Remove("dialogue"); core.Remove("svcObservedDetail");
            context.Facts = core.ToString(Formatting.None);
        }
    }

    private bool BindRecordingGoalInput(SingingGoal goal, string attrs, out string reason)
    {
        reason = "";
        string relation = ReadToolAttribute(attrs, "source_relation");
        string turn = ReadToolAttribute(attrs, "input_turn"), capture = ReadToolAttribute(attrs, "input_capture");
        string clip = ReadToolAttribute(attrs, "input_clip"), quote = ReadToolAttribute(attrs, "source_evidence");
        if (new[] { relation, turn, capture, clip, quote }.All(string.IsNullOrEmpty)) return true; // Legacy tools unchanged.
        if (!int.TryParse(turn, out int inputTurn) || !int.TryParse(capture, out int inputCapture) || inputTurn < 0 || inputCapture < 0)
        { reason = "录音指代事务字段不完整；未登记目标。"; return false; }
        goal.sourceRelation = relation; goal.inputTurn = inputTurn; goal.inputCapture = inputCapture;
        goal.inputClip = clip == "none" ? null : clip; goal.sourceEvidence = quote;
        return ValidateRecordingGoalInput(goal, out reason);
    }

    private bool ValidateRecordingGoalInput(SingingGoal goal, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(goal.sourceRelation)) return true;
        var input = RecordingInputIdentity();
        if (goal.inputTurn != m_SingingGoalTurn || goal.inputCapture != (int?)input["capture"] || goal.inputClip != (string)input["clip"])
        { reason = "录音指代所属输入事务已变化；相同文字不能复用另一轮录音。"; return false; }
        var context = new RecordingSingingDecisionContext { UserText = m_LastUserMsg, CurrentInput = input };
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        foreach (string clip in (goal.refs ?? "").Split(','))
        {
            var value = sense?.DescribeRecordingSingingDecisionFacts(clip.Trim());
            if (value == null) { reason = "所选录音的来源事务已不可用。"; return false; }
            var facts = JObject.FromObject(value);
            context.Materials.Add(new RecordingSingingMaterial { ClipRef = clip.Trim(), CaptureSerial = (int?)facts["CaptureSessionSerial"] ?? 0 });
        }
        if (!RecordingSingingDecisionCompiler.ValidateSourceSelection(context, context.Materials, goal.sourceRelation, goal.sourceEvidence, out _))
        { reason = "录音指代与实际所选来源不一致；current/retained/mixed须按本轮capture和真实refs核对。"; return false; }
        return true;
    }
}
