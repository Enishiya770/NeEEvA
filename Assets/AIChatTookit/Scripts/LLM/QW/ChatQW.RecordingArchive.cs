using System;
using System.Collections;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

public partial class ChatQW
{
    private string BuildRecordingArchiveRequestJson(JObject facts, RecordingArchiveSnapshot archive)
    {
        var sb = new StringBuilder(4096); sb.Append("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":512,\"response_format\":")
            .Append(archive.RetrievalFormat().ToString(Formatting.None)).Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", RecordingArchiveSnapshot.RetrievalContract)); sb.Append(',');
        AppendMessage(sb, new SendData("user", facts.ToString(Formatting.None)));
        sb.Append(']'); AppendReasoningControls(sb, false); AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator PrepareRecordingArchive(RecordingSingingDecisionContext context, int generation,
        Action<RecordingSingingDecisionContext, string> complete, ConversationTrace trace)
    {
        if (generation != m_StreamRequestGeneration) yield break;
        var archive = context.ArchiveSnapshot ?? RecordingArchiveSnapshot.Create(context);
        bool retrieve = context.ArchiveExpansionRequested || RecordingArchiveSnapshot.NeedsRetrieval(context);
        context.ArchiveSnapshot = archive;
        if (archive.TrySessionCandidates(context, out var sessionCandidate, out string sessionFailure))
        {
            sessionCandidate.OnCandidateSnapshot = context.OnCandidateSnapshot;
            sessionCandidate.OnLegacyHandoff = context.OnLegacyHandoff;
            archive.RecordExpandedCandidates(sessionCandidate);
            trace?.Mark("recording_archive.session_bound", new JObject {
                ["binding"] = context.SingingSession?["binding"]?.DeepClone(),
                ["scope"] = "Expand explicit current-input take; neither source approval nor playback." });
            if (generation == m_StreamRequestGeneration) complete(sessionCandidate, null);
            yield break;
        }
        if (sessionFailure != null) { complete(null, sessionFailure); yield break; }
        // A completed playback is evidence in the archive, never an initial
        // candidate filter. A new/continued recording and its introduction must
        // be considered before IR chooses a source, even when the old source is ready.
        if (!retrieve)
        {
            if (!context.ArchiveCandidatesReady)
            {
                if (!archive.TrySelectCandidates(context.Materials.Select(m => m.Key), archive.InputEventIds,
                    out var exact, out string problem)) { complete(null, problem); yield break; }
                exact.OnCandidateSnapshot = context.OnCandidateSnapshot;
                exact.OnLegacyHandoff = context.OnLegacyHandoff;
                archive.RecordExpandedCandidates(exact);
                complete(exact, null); yield break;
            }
            context.ArchiveCandidatesReady = true;
            complete(context, null); yield break;
        }
        JObject page = archive.InitialIndexPage();
        string failure = null;
        int invalidRepairs = 0, noProgressRepairs = 0;
        var visited = context.ArchiveVisitedPages;
        string[] workingKeys = context.ArchiveCandidatesReady ? context.Materials.Select(m => m.Key).ToArray() : new string[0];
        string[] workingEvents = context.ExpandedArchiveInputEvents.OfType<JObject>().Select(e => (string)e["event_id"]).ToArray();
        while (context.ArchiveRetrievalRequests < RecordingArchiveSnapshot.MaximumRetrievalRequests)
        {
            if (generation != m_StreamRequestGeneration) yield break;
            int requestNumber = ++context.ArchiveRetrievalRequests;
            var facts = archive.RetrievalFacts(context, page, failure, RecordingArchiveSnapshot.MaximumRetrievalRequests - requestNumber);
            facts["visited_pages"] = visited.DeepClone();
            facts["working_candidates"] = archive.WorkingIndex(workingKeys, workingEvents);
            string wire = BuildRecordingArchiveRequestJson(facts, archive);
            // A giant current request is not silently sliced into a different
            // intent. Material/event JSON itself has exact paged reads.
            if (wire.Length > 40000) { failure = "archive-current-intent-requires-smaller-scope"; break; }
            JObject decision = null;
            bool transportSucceeded = false;
            var requestTrace = trace?.Child("recording-archive-retrieval");
            using (var request = new UnityWebRequest(url, "POST"))
            {
                m_ActiveStreamRequest = request;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 20;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
                RaiseRequestDiagnostic(wire);
                if (generation != m_StreamRequestGeneration) yield break;
                requestTrace?.Mark("recording_archive.send", new JObject {
                    ["snapshot"] = archive.SnapshotId, ["request"] = requestNumber,
                    ["requestBytes"] = Encoding.UTF8.GetByteCount(wire), ["pageKind"] = page?["kind"], ["page"] = page?["page"],
                    ["scope"] = "read-only evidence retrieval; no material selection or approval" });
                yield return request.SendWebRequest();
                if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;
                if (generation != m_StreamRequestGeneration) yield break;
                failure = "archive-retrieval-transport-failed";
                transportSucceeded = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
                if (transportSucceeded) archive.RecordObservedPage(visited, page);
                if (request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                    TryReadActorIntentEnvelope(request.downloadHandler?.text ?? "", out string raw, out string finish, out failure) && finish == "stop")
                    archive.TryReadRetrieval(raw, out decision, out failure);
                requestTrace?.Mark("recording_archive.received", new JObject {
                    ["snapshot"] = archive.SnapshotId, ["accepted"] = decision != null, ["reason"] = failure,
                    ["httpStatus"] = request.responseCode, ["decision"] = decision });
            }
            if (decision == null)
            {
                // One same-page correction for schema/semantic output failures,
                // within the shared bound. Transport failures are not retried.
                if (transportSucceeded && invalidRepairs++ == 0) continue;
                break;
            }
            workingKeys = ((JArray)decision["keys"]).Values<string>().Distinct().ToArray();
            workingEvents = ((JArray)decision["event_ids"]).Values<string>().Distinct().ToArray();
            string operation = (string)decision["operation"];
            if (operation == "unresolved") { failure = "archive-evidence-unresolved: " + (string)decision["missing"]; break; }
            if (operation == "select")
            {
                if (context.ArchiveExpansionRequested && !archive.AddsUnexpandedEvidence(context, workingKeys, workingEvents))
                {
                    failure = "archive-expansion-no-new-evidence";
                    trace?.Sample("recording_archive.no_progress", new JObject {
                        ["snapshot"] = archive.SnapshotId, ["request"] = requestNumber,
                        ["reason"] = failure, ["keys"] = new JArray(workingKeys),
                        ["scope"] = "Same immutable source/event facts already supplied in full; no second identical IR pass." });
                    break;
                }
                if (archive.TrySelectCandidates(workingKeys, workingEvents, out var candidate, out failure))
                {
                    candidate.ArchiveRetrievalRequests = context.ArchiveRetrievalRequests;
                    candidate.ArchiveExpansionRequested = false;
                    candidate.ArchiveExpansionRequest = context.ArchiveExpansionRequest == null ? null : (JObject)context.ArchiveExpansionRequest.DeepClone();
                    candidate.ArchiveVisitedPages = (JArray)context.ArchiveVisitedPages.DeepClone();
                    archive.RecordExpandedCandidates(candidate);
                    candidate.OnCandidateSnapshot = context.OnCandidateSnapshot;
                    candidate.OnLegacyHandoff = context.OnLegacyHandoff;
                    // Same request recovery facts are current; source PCM facts
                    // remain from the immutable archive, never a live re-read.
                    candidate.Facts = context.Facts;
                    if (RecordingSingingDecisionCompiler.Prompt(candidate).Length > RecordingSingingDecisionCompiler.MaximumProtectedPromptCharacters)
                    { failure = "archive-selected-recovery-evidence-requires-smaller-scope"; continue; }
                    trace?.Mark("recording_archive.candidates", new JObject {
                        ["snapshot"] = archive.SnapshotId, ["retained"] = archive.MaterialCount,
                        ["candidates"] = candidate.Materials.Count, ["keys"] = new JArray(workingKeys),
                        ["acceptedInputEvents"] = workingEvents.Length, ["requests"] = requestNumber });
                    if (generation == m_StreamRequestGeneration) complete(candidate, null);
                    yield break;
                }
                // The model can request exact evidence pages or a smaller set;
                // over-budget selected facts are not shortened behind its back.
                continue;
            }
            JObject next = operation == "index" ? archive.IndexPage((int)decision["page"]) : null;
            if (operation == "search") next = archive.SearchIndexPage((string)decision["query"], (int)decision["page"]);
            if (operation == "evidence") archive.TryEvidencePage((string)decision["key"], (string)decision["event_id"], (int)decision["page"], out next);
            if (next == null) { failure = "archive-page-not-found"; continue; }
            if (archive.HasObservedPage(visited, next))
            {
                failure = "archive-page-already-observed-select-or-request-new-evidence";
                trace?.Sample("recording_archive.no_progress", new JObject {
                    ["snapshot"] = archive.SnapshotId, ["request"] = requestNumber,
                    ["reason"] = failure, ["page"] = archive.PageIdentity(next) });
                // One bounded corrective selection from cached facts; a repeated
                // request is not more evidence and must not consume 16 HTTPs.
                if (noProgressRepairs++ > 0) break;
                page = next;
                continue;
            }
            page = next; failure = null;
        }
        if (generation == m_StreamRequestGeneration) complete(null, failure ?? "archive-retrieval-limit-unexpanded-evidence");
    }

    private string RecordingArchiveIncompleteReply(RecordingSingingDecisionContext context, string reason)
    {
        // A factual boundary statement, not a planner-created refusal or an
        // assertion that a retained recording does not exist.
        return "<lang code=\"zh\"/><speech mode=\"independent\"/><say>这次需要核对的录音资料还没核对完整。请指出要用的片段，或缩小这次要核对的范围，我再继续。</say>";
    }

    private static void AddRecordingRejectionResolutionSchema(JObject schema)
    {
        schema["properties"]["singing_goal_expected"]["properties"]["boundary_resolution"] = new JObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject {
                ["ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16,
                    ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 80 } },
                ["quote"] = new JObject { ["type"] = "string", ["minLength"] = 2, ["maxLength"] = 1024 },
                ["reason"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 400 }
            }, ["required"] = new JArray("ids", "quote", "reason") };
        schema["properties"]["singing_goal_expected"]["properties"]["prior_rejection_resolution"] = new JObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject {
                ["id"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 80 },
                ["basis"] = new JObject { ["type"] = "string", ["enum"] = new JArray("goal_changed", "source_evidence_changed") },
                ["quote"] = new JObject { ["type"] = "string", ["maxLength"] = 1024 },
                ["reason"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 400 }
            }, ["required"] = new JArray("id", "basis", "quote", "reason")
        };
    }
}
