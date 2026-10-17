using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private readonly Dictionary<string,JArray> m_WorkResultPlaybackAudio=new Dictionary<string,JArray>();
    private long m_WorkPlaybackSerial;
    private int m_WorkDeliveryEpoch = -1, m_WorkDeliveryTurn = -1;
    private float m_WorkDeliverySession = -1;
    private readonly Dictionary<string, string> m_WorkDeliveredResults = new Dictionary<string, string>();
    private readonly List<string> m_WorkCompletedSpeech = new List<string>();
    private readonly Dictionary<string, string> m_WorkResultPlaybackText = new Dictionary<string, string>();
    private readonly Dictionary<string, string> m_WorkDeliveredPlayback = new Dictionary<string, string>();
    private readonly Dictionary<string, int> m_WorkDeliveredPlaybackLengths = new Dictionary<string, int>();
    private readonly Dictionary<string, JObject> m_WorkToolResultReceipts = new Dictionary<string, JObject>();
    private readonly Dictionary<string, int> m_WorkCorrectionStarts = new Dictionary<string, int>();
    private readonly Dictionary<string, string> m_WorkCorrectionGroups = new Dictionary<string, string>();
    private void RecordWorkCorrectionBoundary(ActivityMonitorProtocol.SpeechUnit unit)
    {
        EnsureWorkDeliveryOwner();
        string id=unit?.CorrectedResultId;
        if(string.IsNullOrEmpty(id) || m_WorkCorrectionGroups.TryGetValue(id,out var group)&&group==unit.CorrectionGroup)return;
        // Called by the actual player before its completed text is appended.
        // An earlier positive quote cannot settle audio after a scoped retraction.
        m_WorkCorrectionGroups[id]=unit.CorrectionGroup;
        m_WorkCorrectionStarts[id]=m_WorkResultPlaybackText.TryGetValue(id,out var played)?played.Length:0;
    }
    private bool WorkResultDeliveryCurrent(string id) => m_WorkDeliveredResults.ContainsKey(id);
    private bool WorkResultPlaybackNeedsReview(string id) => m_WorkDeliveredResults.ContainsKey(id) &&
        m_WorkDeliveredPlayback.TryGetValue(id,out var audited) &&
        audited!=ActivityEvidencePolicy.Stamp(new JValue(m_WorkResultPlaybackText.TryGetValue(id,out var played)?played:""));

    // Work ownership outlives a single response generation. Questions are
    // reporting obligations only and cannot authorize any action.
    private JArray ReadWorkAnswerObligations(bool pendingOnly)
    {
        EnsureWorkDeliveryOwner();
        var semantics = m_WorkRequestSemantics;
        var sequence = ReadRoomTaskSequence();
        bool classified = semantics != null && (int?)semantics["workEpoch"] == m_WorkEpoch &&
            (int?)semantics["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
            (string)semantics["currentUserText"] == m_ActionConstraints.CurrentUserText &&
            m_WorkRequestSemanticsModel == m_ChatSettings?.m_ChatModel;
        if (sequence == null && !classified) return new JArray();
        var questions = sequence?["answerObligations"] is JArray planned ? (JArray)planned.DeepClone() :
            ReplyAnswerObligationProtocol.FromSemantics(m_ActionConstraints.CurrentUserText, classified ? semantics : null);
        return new JArray(questions.OfType<JObject>().Where(q => !pendingOnly ||
            !WorkResultDeliveryCurrent((string)ReplyAnswerObligationProtocol.DeliveryResult(q)["id"])));
    }

    private void EnsureWorkDeliveryOwner()
    {
        if (m_WorkDeliveryEpoch == m_WorkEpoch && m_WorkDeliveryTurn == m_ActionConstraints.CurrentUserTurn &&
            m_WorkDeliverySession == m_AgentSessionStartTime) return;
        m_WorkDeliveryEpoch = m_WorkEpoch; m_WorkDeliveryTurn = m_ActionConstraints.CurrentUserTurn;
        m_WorkDeliverySession = m_AgentSessionStartTime;
        m_WorkDeliveredResults.Clear(); m_WorkCompletedSpeech.Clear(); m_WorkResultPlaybackText.Clear(); m_WorkResultPlaybackAudio.Clear(); m_WorkDeliveredPlayback.Clear();
        m_WorkToolResultReceipts.Clear();
        m_WorkDeliveredPlaybackLengths.Clear();
        m_WorkCorrectionStarts.Clear();m_WorkCorrectionGroups.Clear();
    }

    private void RecordWorkCompletedSpeech(string text)
        => RecordWorkCompletedSpeechForResult(text, null);

    private void RecordWorkCompletedSpeechForResult(string text, string resultId)
    {
        EnsureWorkDeliveryOwner();
        if (string.IsNullOrWhiteSpace(text)) return;
        // Bind completed audio to the result versions that actually existed at
        // playback. A later version cannot borrow the same words from an old one.
        foreach (var result in ((JArray)ReadWorkDelivery()["results"]).OfType<JObject>())
        {
            string id = (string)result["id"];
            // A validated, scoped delivery supplement cannot retract a different
            // result. Ordinary speech remains eligible for semantic retraction review.
            if (!string.IsNullOrEmpty(resultId) && id != resultId) continue;
            m_WorkResultPlaybackText.TryGetValue(id, out string played);
            m_WorkResultPlaybackText[id] = (played ?? "") + text;
            // Later audio may need an audit, but does not itself create another
            // reporting obligation or reopen the already completed user work.
        }
        m_WorkCompletedSpeech.Add(text);
    }

    private void RecordWorkCompletedAudio(string text,string resultId,ActivityMonitorProtocol.SpeechUnit unit,int audioVersion)
    {
        // Invoked only by a player's successful whole-chunk completion, after
        // text was committed. A cancelled/prefetched chunk never enters here.
        string audioId=unit?.Id??("audio:"+m_FormalResponseGeneration+":"+(++m_WorkPlaybackSerial));
        foreach(var result in ((JArray)ReadWorkDelivery()["results"]).OfType<JObject>()) {
            string id=(string)result["id"];
            if(!string.IsNullOrEmpty(resultId)&&id!=resultId)continue;
            if(!m_WorkResultPlaybackText.TryGetValue(id,out var played)||!played.EndsWith(text,StringComparison.Ordinal))continue;
            if(!m_WorkResultPlaybackAudio.TryGetValue(id,out var rows))m_WorkResultPlaybackAudio[id]=rows=new JArray();
            rows.Add(new JObject{["audioId"]=audioId,["speechVersion"]=audioVersion,["generation"]=m_FormalResponseGeneration,
                ["textStart"]=played.Length-text.Length,["textLength"]=text.Length,["startedAtRealtime"]=unit?.StartedAtRealtime,
                ["completedAtRealtime"]=Time.realtimeSinceStartup,["wholeTextPlayed"]=true});
        }
    }

    private JObject ReadWorkDelivery()
    {
        EnsureWorkDeliveryOwner();
        var results = new JArray();
        var sequence = ReadRoomTaskSequence();
        if (sequence?["steps"] is JArray steps)
            foreach (var step in steps.OfType<JObject>().Where(s => TaskAttemptSettlementPolicy.Terminal((string)s["status"])))
                results.Add(WorkResultDeliveryProtocol.Result("step:" + (string)step["id"], (string)step["status"],
                    PickRoomFields(step, "id", "kind", "actionId", "goalId", "desiredState", "receipt")));
        if (results.Count == 0)
        {
            var owned = BuildWorkRoomCompletionObservation();
            var execution = owned?.SelectToken("currentRoomFacts.execution") as JObject;
            if (!string.IsNullOrEmpty((string)owned?["actionId"]) && (string)owned["actionId"] == (string)execution?["actionId"] &&
                (RoomMovementResultPolicy.IsTerminalFailure((string)execution["phase"]) || new[] { "arrived", "stopped", "cancelled" }.Contains((string)execution["phase"])))
                results.Add(WorkResultDeliveryProtocol.Result("action:" + (string)owned["actionId"], (string)execution["phase"],
                    PickRoomFields(execution, "actionId", "phase", "reason", "operationRevision")));
        }
        // General tool results use their own versioned payload, not a lamp/movement classifier.
        var intervention = ReadActivityInterventionResult();
        if(intervention!=null)results.Add(intervention.DeepClone());
        void Tool(string source, bool pending, string value)
        { if (pending && !string.IsNullOrEmpty(value)) m_WorkToolResultReceipts[source]=WorkResultDeliveryProtocol.Result(source, "returned", new JValue(value)); }
        Tool("selfInspection", !string.IsNullOrEmpty(m_PendingSelfInspection), m_PendingSelfInspection);
        Tool("songSearch", m_SongSearchResultPending, m_LastSongSearchResult);
        Tool("songCatalog", m_SongCatalogResultPending, m_LastSongCatalogResult);
        Tool("songMemory", m_SongMemoryResultPending, m_LastSongMemoryResult);
        Tool("speakerManage", m_SpeakerManageResultPending, m_LastSpeakerManageResult);
        Tool("practiceDrop", m_PracticeDropResultPending, m_LastPracticeDropResult);
        Tool("practiceEdit", m_PracticeEditResultPending, m_LastPracticeEditResult);
        Tool("humBack", m_HumBackResultPending, m_LastHumBackResult);
        foreach(var retained in m_WorkToolResultReceipts.Values)results.Add(retained.DeepClone());
        foreach (var question in ReadWorkAnswerObligations(false).OfType<JObject>())
            results.Add(ReplyAnswerObligationProtocol.DeliveryResult(question));
        foreach (var result in results.OfType<JObject>())
        {
            // The canonical sequence already carries the full immutable receipt.
            // Delivery classification needs its identity/status, not another copy
            // of navigation geometry and device search history.
            if (((string)result["source"]).StartsWith("step:", StringComparison.Ordinal))
            {
                var facts = (JObject)result["facts"];
                result["facts"] = WorkResultDeliveryProtocol.StepSummary(facts);
                result["canonicalReceipt"] = "taskSequence.steps[" + (string)facts["id"] + "].receipt";
            }
            m_WorkDeliveredResults.TryGetValue((string)result["id"], out string quote);
            bool delivered = WorkResultDeliveryCurrent((string)result["id"]);
            result["previouslyDeliveredQuote"] = quote??"";
            result["delivered"] = delivered; result["deliveredQuote"] = delivered ? quote : "";
            result["playbackReviewPending"] = WorkResultPlaybackNeedsReview((string)result["id"]);
            result["hasCompletedPlaybackAfterResult"] = m_WorkResultPlaybackText.ContainsKey((string)result["id"]);
            result["completedPlaybackText"] = m_WorkResultPlaybackText.TryGetValue((string)result["id"], out var versionSpeech) ? versionSpeech : "";
            result["playbackRevision"] = ActivityEvidencePolicy.Stamp(result["completedPlaybackText"]);
            if(m_WorkResultPlaybackAudio.TryGetValue((string)result["id"],out var audio)) {
                result["completedAudio"]=new JArray(audio.Skip(Math.Max(0,audio.Count-12)).Select(r=>r.DeepClone()));
                result["completedAudioCount"]=audio.Count;
                result["audioScope"]="Actual whole chunks for this result version; textStart/textLength index completedPlaybackText. Latest 12 metadata rows; all rows retained by runtime. Not proof that every phrase describes this result.";
            }
            result["requiresReportAct"] = true;
            if (m_WorkDeliveredPlaybackLengths.TryGetValue((string)result["id"], out int auditedLength))
                result["reviewedPlaybackLength"] = auditedLength;
            if(m_WorkCorrectionStarts.TryGetValue((string)result["id"],out int boundary))result["latestCorrectionStart"]=boundary;
        }
        string played = string.Concat(m_WorkCompletedSpeech);
        // Historical formal receipts remain in the work facts. Without a bound
        // result version they cannot certify delivery of a newer result.
        return new JObject { ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["results"] = results, ["completedPlaybackText"] = played,
            ["scope"] = "Versioned result delivery only. Delivered failure remains a failure; no retry authority. Quotes come from completed audio chunks, never generated text. Independent unanswered requests remain separate." };
    }

    private bool ApplyWorkDelivery(AutonomyIntentDecision decision, JObject sequence)
    {
        var delivery = ReadWorkDelivery(); var results = (JArray)delivery["results"];
        var checks = new JArray();
        foreach (var report in decision.delivery_reports ?? new WorkResultDeliveryProtocol.Report[0])
        {
            if (report == null) { checks.Add(new JObject { ["rejection"] = "report-missing" }); continue; }
            var result = results.OfType<JObject>().FirstOrDefault(r => (string)r["id"] == report.id);
            m_WorkResultPlaybackText.TryGetValue(report.id ?? "", out string completedForVersion);
            string rejection = WorkResultDeliveryProtocol.CoverageRejection(result, report, completedForVersion);
            if (WorkResultDeliveryProtocol.RetractionProven(result, report, completedForVersion))
            {
                m_WorkDeliveredResults.Remove(report.id); m_WorkDeliveredPlayback.Remove(report.id);
                result["delivered"] = false; result["playbackReviewPending"] = false;
            }
            checks.Add(new JObject { ["id"] = report.id, ["quote"] = report.quote, ["comparison"] = report.comparison, ["coverage"] = report.coverage,
                ["playbackEvidence"] = ResultPlaybackSelection.Evidence(result,report.lastRelevantQuote,report.selectedPlaybackId),
                ["source"] = result?["source"]?.DeepClone(), ["accepted"] = rejection == null, ["rejection"] = rejection,
                ["completedPlaybackForVersion"] = completedForVersion ?? "" });
            if (rejection != null) continue;
            m_WorkDeliveredResults[report.id] = report.quote;
            m_WorkDeliveredPlayback[report.id] = (string)result["playbackRevision"];
            m_WorkDeliveredPlaybackLengths[report.id] = (completedForVersion ?? "").Length;
            result["delivered"] = true; result["deliveredQuote"] = report.quote;
            result["playbackReviewPending"] = false;
            AcknowledgeDeliveredToolResult((string)result["source"]);
        }
        foreach (var result in results.OfType<JObject>().Where(r => (bool?)r["delivered"] != true &&
            !(decision.delivery_reports ?? new WorkResultDeliveryProtocol.Report[0]).Any(row => row?.id == (string)r["id"])))
            checks.Add(new JObject { ["id"] = result["id"].DeepClone(), ["source"] = result["source"].DeepClone(), ["rejection"] = "report-missing" });
        if (WorkResultDeliveryProtocol.IsSettledActionQuote(decision.remaining_request_quote, sequence,
            results.OfType<JObject>().Where(r => (bool?)r["delivered"] == true).Select(r => (string)r["source"])))
            decision.remaining_request_quote = "";
        bool settled = WorkResultDeliveryProtocol.CanClose(results, sequence, decision.remaining_request_quote) && !HasWorkToolInFlight() &&
            !NeedsSingingReviewDetails();
        if (settled && decision.next_intent_source != "autonomous")
        {
            decision.work_status = "closed"; decision.proceed = false; decision.intent = "";
            decision.next_step_id = ""; decision.next_intent_source = "none";
        }
        else if (WorkResultDeliveryProtocol.MustContinue(decision.work_status, results,
            decision.remaining_request_quote, m_ActionConstraints.CurrentUserText))
        {
            // A model closure cannot erase unplayed results or an independent answer.
            // Keep this continuation reply-only; this ledger never authorizes actions.
            decision.work_status = "continue"; decision.proceed = true;
            decision.next_step_id = ""; decision.next_intent_source = "current-work";
            decision.planning_focus = "other";
            decision.intent = "对照当前原请求、结果版本与实际已播内容，补齐尚未交付的结果或独立回答；不重复已经告知的结果，不重执行动作。";
        }
        Debug.Log("[Work/ResultDelivery] " + new JObject { ["workEpoch"] = m_WorkEpoch,
            ["userTurn"] = m_ActionConstraints.CurrentUserTurn, ["settled"] = settled,
            ["remainingRequestQuote"] = decision.remaining_request_quote ?? "", ["results"] = results,
            ["reports"] = JArray.FromObject(decision.delivery_reports ?? new WorkResultDeliveryProtocol.Report[0]), ["coverageChecks"] = checks,
            ["scope"] = "Execution outcomes unchanged; only completed spoken coverage is settled." }.ToString(Formatting.None), this);
        return settled;
    }

    private void AcknowledgeDeliveredToolResult(string source)
    {
        // Called only after matching the current payload's version and completed
        // audio evidence. Preserve historical results and other pending tools.
        switch (source)
        {
            case "selfInspection": m_PendingSelfInspection = ""; m_SelfInspectionDeliveryAttempts = 0; break;
            case "songSearch": m_SongSearchResultPending = false; break;
            case "songCatalog": m_SongCatalogResultPending = false; break;
            case "songMemory": m_SongMemoryResultPending = false; break;
            case "speakerManage": m_SpeakerManageResultPending = false; break;
            case "practiceDrop": m_PracticeDropResultPending = false; break;
            case "practiceEdit": m_PracticeEditResultPending = false; break;
            case "humBack": m_HumBackResultPending = false; break;
        }
    }

    private bool UnsuccessfulStepReportDelivered(JObject step)
    {
        string status=(string)step?["status"];
        if(status!="failed" && status!="blocked")return false;
        EnsureWorkDeliveryOwner();
        var result = WorkResultDeliveryProtocol.Result("step:" + (string)step["id"], status,
            PickRoomFields(step, "id", "kind", "actionId", "goalId", "desiredState", "receipt"));
        return WorkResultDeliveryCurrent((string)result["id"]) && !WorkResultPlaybackNeedsReview((string)result["id"]);
    }
}
