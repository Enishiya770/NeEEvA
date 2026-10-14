using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_WorkDeliveryEpoch = -1, m_WorkDeliveryTurn = -1;
    private float m_WorkDeliverySession = -1;
    private readonly Dictionary<string, string> m_WorkDeliveredResults = new Dictionary<string, string>();
    private readonly List<string> m_WorkCompletedSpeech = new List<string>();
    private readonly Dictionary<string, string> m_WorkResultPlaybackText = new Dictionary<string, string>();

    private void EnsureWorkDeliveryOwner()
    {
        if (m_WorkDeliveryEpoch == m_WorkEpoch && m_WorkDeliveryTurn == m_ActionConstraints.CurrentUserTurn &&
            m_WorkDeliverySession == m_AgentSessionStartTime) return;
        m_WorkDeliveryEpoch = m_WorkEpoch; m_WorkDeliveryTurn = m_ActionConstraints.CurrentUserTurn;
        m_WorkDeliverySession = m_AgentSessionStartTime;
        m_WorkDeliveredResults.Clear(); m_WorkCompletedSpeech.Clear(); m_WorkResultPlaybackText.Clear();
    }

    private void RecordWorkCompletedSpeech(string text)
    {
        EnsureWorkDeliveryOwner();
        if (string.IsNullOrWhiteSpace(text)) return;
        // Bind completed audio to the result versions that actually existed at
        // playback. A later version cannot borrow the same words from an old one.
        foreach (var result in ((JArray)ReadWorkDelivery()["results"]).OfType<JObject>())
        {
            string id = (string)result["id"];
            m_WorkResultPlaybackText.TryGetValue(id, out string played);
            m_WorkResultPlaybackText[id] = (played ?? "") + text;
        }
        m_WorkCompletedSpeech.Add(text);
    }

    private JObject ReadWorkDelivery()
    {
        EnsureWorkDeliveryOwner();
        var results = new JArray();
        var sequence = ReadRoomTaskSequence();
        if (sequence?["steps"] is JArray steps)
            foreach (var step in steps.OfType<JObject>().Where(s => new[] { "completed", "failed" }.Contains((string)s["status"])))
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
        void Tool(string source, bool pending, string value)
        { if (pending && !string.IsNullOrEmpty(value)) results.Add(WorkResultDeliveryProtocol.Result(source, "returned", new JValue(value))); }
        Tool("selfInspection", !string.IsNullOrEmpty(m_PendingSelfInspection), m_PendingSelfInspection);
        Tool("songSearch", m_SongSearchResultPending, m_LastSongSearchResult);
        Tool("songCatalog", m_SongCatalogResultPending, m_LastSongCatalogResult);
        Tool("songMemory", m_SongMemoryResultPending, m_LastSongMemoryResult);
        Tool("speakerManage", m_SpeakerManageResultPending, m_LastSpeakerManageResult);
        Tool("practiceDrop", m_PracticeDropResultPending, m_LastPracticeDropResult);
        Tool("practiceEdit", m_PracticeEditResultPending, m_LastPracticeEditResult);
        Tool("humBack", m_HumBackResultPending, m_LastHumBackResult);
        foreach (var result in results.OfType<JObject>())
        {
            // The canonical sequence already carries the full immutable receipt.
            // Delivery classification needs its identity/status, not another copy
            // of navigation geometry and device search history.
            if (((string)result["source"]).StartsWith("step:", StringComparison.Ordinal))
            {
                var facts = (JObject)result["facts"];
                var receiptFacts = facts["receipt"] as JObject;
                result["facts"] = PickRoomFields(facts, "id", "kind", "actionId", "goalId", "desiredState");
                result["facts"]["outcome"] = PickRoomFields(receiptFacts,
                    "phase", "status", "reason", "observedUtc", "stateChanged", "entityId");
                result["canonicalReceipt"] = "taskSequence.steps[" + (string)facts["id"] + "].receipt";
            }
            bool delivered = m_WorkDeliveredResults.TryGetValue((string)result["id"], out string quote);
            result["delivered"] = delivered; result["deliveredQuote"] = delivered ? quote : "";
            result["hasCompletedPlaybackAfterResult"] = m_WorkResultPlaybackText.ContainsKey((string)result["id"]);
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
        foreach (var report in decision.delivery_reports ?? new WorkResultDeliveryProtocol.Report[0])
        {
            var result = results.OfType<JObject>().FirstOrDefault(r => (string)r["id"] == report.id);
            m_WorkResultPlaybackText.TryGetValue(report.id ?? "", out string completedForVersion);
            if (!WorkResultDeliveryProtocol.Covers(result, report, completedForVersion)) continue;
            m_WorkDeliveredResults[report.id] = report.quote;
            result["delivered"] = true; result["deliveredQuote"] = report.quote;
            AcknowledgeDeliveredToolResult((string)result["source"]);
        }
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

    private bool FailedStepReportDelivered(JObject step)
    {
        if ((string)step?["status"] != "failed") return false;
        EnsureWorkDeliveryOwner();
        var result = WorkResultDeliveryProtocol.Result("step:" + (string)step["id"], "failed",
            PickRoomFields(step, "id", "kind", "actionId", "goalId", "desiredState", "receipt"));
        return m_WorkDeliveredResults.ContainsKey((string)result["id"]);
    }
}
