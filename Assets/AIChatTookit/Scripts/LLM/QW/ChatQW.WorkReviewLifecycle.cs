using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private sealed class WorkReviewTrace
    {
        internal string Id = Guid.NewGuid().ToString("N"), ReviewId, Terminal, CancelReason;
        internal int Generation, Attempt, RequestBytes;
        internal float CreatedAt;
        internal bool Sent;
        internal JObject Owner;
        internal JArray ResultIds;
        internal UnityWebRequest Request;
    }
    private WorkReviewTrace m_WorkReviewTrace;

    private void TraceWorkReview(WorkReviewTrace trace, string stage, string status)
    {
        var request = trace.Request;
        // This local terminal never asserts that an aborted server computation
        // stopped. HTTP completion is observed separately, even for stale owners.
        Debug.Log("[Agent/WorkReviewLifecycle] " + new JObject {
            ["reviewId"] = trace.ReviewId, ["requestId"] = trace.Id,
            ["generation"] = trace.Generation, ["currentGeneration"] = m_EphemeralGeneration,
            ["owner"] = trace.Owner?.DeepClone(), ["expectedResultIds"] = trace.ResultIds?.DeepClone(),
            ["attempt"] = trace.Attempt, ["stage"] = stage, ["status"] = status,
            ["ownerCurrent"] = trace.Generation == m_EphemeralGeneration,
            ["httpRequestSent"] = trace.Sent, ["requestBytes"] = trace.RequestBytes,
            ["elapsedSeconds"] = Time.realtimeSinceStartup - trace.CreatedAt,
            ["cancelReason"] = trace.CancelReason, ["localTerminal"] = trace.Terminal,
            ["transportFinalKnown"] = trace.Sent && request != null && request.isDone,
            ["httpStatus"] = request == null ? 0 : request.responseCode,
            ["transportResult"] = request?.result.ToString(), ["transportError"] = request?.error,
            ["receivedBytes"] = request == null ? 0UL : request.downloadedBytes,
            ["serverRequestId"] = request != null && request.isDone ? request.GetResponseHeader("x-request-id") : null
        }.ToString(Formatting.None));
    }

    private WorkReviewTrace BeginWorkReviewTrace(string json, int generation, int attempt, JObject receipt, string reviewId)
    {
        var trace = new WorkReviewTrace { Generation = generation, Attempt = attempt,
            CreatedAt = Time.realtimeSinceStartup, RequestBytes = Encoding.UTF8.GetByteCount(json ?? "") };
        trace.ReviewId = reviewId ?? trace.Id;
        BindWorkReviewTrace(trace, receipt);
        // A stale coroutine may be inspected for diagnosis but cannot replace
        // the live request or its cancellation owner.
        if (generation == m_EphemeralGeneration) m_WorkReviewTrace = trace;
        TraceWorkReview(trace, "created", "pending");
        return trace;
    }

    private static void BindWorkReviewTrace(WorkReviewTrace trace, JObject receipt)
    {
        trace.Owner = receipt == null ? null : new JObject {
            ["workEpoch"] = receipt["workEpoch"]?.DeepClone(), ["userTurn"] = receipt["userTurn"]?.DeepClone() };
        trace.ResultIds = receipt?["results"] is JArray results
            ? new JArray(WorkResultDeliveryProtocol.ReviewIds(results)) : null;
    }

    private void EndWorkReviewTrace(WorkReviewTrace trace, string status)
    {
        if (trace.Terminal != null) return;
        trace.Terminal = status;
        TraceWorkReview(trace, "terminal", status);
        if (ReferenceEquals(m_WorkReviewTrace, trace)) m_WorkReviewTrace = null;
    }

    private void CancelWorkReviewTrace(string reason)
    {
        var trace = m_WorkReviewTrace;
        if (trace == null) return;
        trace.CancelReason = string.IsNullOrWhiteSpace(reason) ? "auxiliary-owner-cancelled" : reason;
        TraceWorkReview(trace, "cancel-requested", "cancelled");
        EndWorkReviewTrace(trace, "cancelled");
    }
}
