using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class RecordingArchiveRegression
{
    private static int count;
    private static void Check(bool value, string message)
    { count++; if (!value) throw new InvalidOperationException("Recording archive: " + message); }

    private static RecordingSingingDecisionContext Archive(int materials)
    {
        var c = new RecordingSingingDecisionContext {
            UserText = "把最开始的第一段和刚才纠正过的第三段连起来唱，保留完整目标。",
            CurrentInput = new JObject { ["turn"] = 120, ["capture"] = 120, ["clip"] = "clip:120" },
            CurrentIntent = new JObject { ["roleProposal"] = new JObject { ["task"] = "recording_singing", ["goal"] = "第一段及纠正后的第三段" }, ["personaEvidence"] = "角色明确选择的完整原文" },
            RequireGroundedResolution = true,
            Facts = new JObject { ["reviewRecovery"] = new JObject { ["status"] = "revise", ["expected"] = "独立复核仍需证据" } }.ToString() };
        for (int i = 1; i <= materials; i++)
        {
            string text = i == 1 ? "这是很早的第一段介绍；声学分数低不代表没有歌声。" :
                i == 80 ? "这次纠正：前面是第三段的开头，现在接着唱完后半段。" : "普通讲话与录音事件" + i;
            c.Materials.Add(new RecordingSingingMaterial { Key = "clip:" + i, ClipRef = "clip:" + i,
                CaptureSerial = i, RecordingSequence = i, Revision = "pending", PlaybackStatus = "evidence_only",
                HasCleanRange = true, HasExpandedRange = true,
                Description = new JObject { ["RecordingTranscript"] = text, ["SingingSegmentText"] = "",
                    ["AcousticMode"] = "speech", ["SingingProbability"] = 0.01,
                    ["RawSeconds"] = 12, ["CleanStartSeconds"] = 0, ["CleanEndSeconds"] = 12,
                    ["ExpandedStartSeconds"] = 0, ["ExpandedEndSeconds"] = 12,
                    ["RawAudioRetained"] = true, ["UnknownField"] = JValue.CreateNull(),
                    ["pcmLineage"] = new JObject { ["known"] = true, ["regions"] = new JArray(new JObject {
                        ["sourceId"] = "pcm:" + i, ["sourceCaptureSerial"] = i,
                        ["sourceStartSeconds"] = 0, ["sourceEndSeconds"] = 12,
                        ["rawStartSeconds"] = 0, ["rawEndSeconds"] = 12 }) } }.ToString(Formatting.None) });
            c.ArchiveInputEvents.Add(new JObject { ["turn"] = i, ["capture"] = i, ["clip"] = "clip:" + i,
                ["text"] = text, ["timeOrder"] = new JArray(new JObject { ["text"] = text, ["start"] = 0, ["end"] = 12 }) });
        }
        return c;
    }

    private static JObject Decision(RecordingArchiveSnapshot archive, string operation, string[] keys = null) => new JObject {
        ["snapshot_id"] = archive.SnapshotId, ["operation"] = operation, ["page"] = 0, ["query"] = "",
        ["key"] = "", ["event_id"] = "", ["keys"] = new JArray(keys ?? new string[0]),
        ["event_ids"] = new JArray(), ["missing"] = "" };

    public static int RunChecks()
    {
        count = 0;
        var original = Archive(120);
        Check(RecordingArchiveSnapshot.NeedsRetrieval(original), "120 sources require retrieval, not 32k silent closure");
        var archive = RecordingArchiveSnapshot.Create(original);
        Check(archive.MaterialCount == 120 && archive.InputEventCount == 120, "complete material and ordinary-input archive retained");
        Check(archive.IndexPageCount > 1, "100+ evidence indexed into bounded pages");
        var all = new JArray();
        for (int page = 0; page < archive.IndexPageCount; page++)
        {
            var result = archive.IndexPage(page);
            Check((int)result["page"] == page && (int)result["page_count"] == archive.IndexPageCount, "page identity and known total");
            foreach (var row in (JArray)result["entries"]) all.Add(row.DeepClone());
        }
        Check(all.OfType<JObject>().Count(r => (string)r["kind"] == "recording") == 120, "no latest-N source loss");
        Check(all.OfType<JObject>().Count(r => (string)r["kind"] == "accepted_input") == 120, "ordinary introductions/corrections all indexed");
        Check(all.OfType<JObject>().Any(r => (string)r["key"] == "clip:1"), "old low-acoustic first source remains discoverable");
        Check(archive.IndexPage(-1) == null && archive.IndexPage(archive.IndexPageCount) == null, "out-of-range pages explicit");
        Check(archive.TrySelectCandidates(new[] { "clip:1", "clip:80", "clip:120" }, new[] { "input:0", "input:79" }, out var candidate, out var error), "model-selected nonrecent candidates expand: " + error);
        Check(candidate.Materials.Select(m => m.Key).SequenceEqual(new[] { "clip:1", "clip:80", "clip:120" }), "candidate archive chronology, not an invented playback order");
        Check(candidate.Materials.All(m => !m.RequiresExpansion), "IR candidates have full source evidence");
        Check(candidate.ExpandedArchiveInputEvents.Count == 2 && candidate.ArchiveInputEvents.Count == 120, "selected full context events plus retained complete original history");
        Check(candidate.CurrentIntent.ToString() == original.CurrentIntent.ToString() && candidate.UserText == original.UserText, "full role choice and current request preserved");
        Check(RecordingSingingDecisionCompiler.Prompt(candidate).Length < 32000, "candidate IR bounded without chopping facts");
        Check(!RecordingArchiveSnapshot.NeedsRetrieval(candidate), "ready candidate skips extra retrieval pass");
        var review = archive.CandidateReviewFacts(candidate);
        Check((string)review["archive_snapshot_id"] == archive.SnapshotId && ((JArray)review["candidate_recording_facts"]).Count == 3, "independent review shares source snapshot and competing candidates");
        Check((int)review["archive_index"]["unexpanded_material_count"] == 117, "unexpanded explicitly unknown, never missing");
        Check(review["candidate_recording_facts"][0]["UnknownField"].Type == JTokenType.Null, "full review retains explicit null");
        Check((string)review["candidate_recording_facts"][0]["RecordingTranscript"] == (string)JObject.Parse(candidate.Materials[0].Description)["RecordingTranscript"], "IR and review transcript byte content identical");
        Check(JToken.DeepEquals(review["candidate_recording_facts"][0]["pcmLineage"], JObject.Parse(candidate.Materials[0].Description)["pcmLineage"]), "PCM lineage survives review projection");
        var projected = RecordingSingingDecisionCompiler.PromptFacts(candidate);
        Check(projected["facts"]["reviewRecovery"] != null, "review recovery facts not discarded");
        Check((string)projected["archive_index"]["snapshot_id"] == archive.SnapshotId, "IR same snapshot marker");
        string frozen = (string)review["candidate_recording_facts"][0]["RecordingTranscript"];
        original.Materials[0].Description = "{}"; original.CurrentIntent["personaEvidence"] = "mutated"; original.ArchiveInputEvents.Clear();
        Check(archive.TryGetMaterial("clip:1", out var reread) && (string)JObject.Parse(reread.Description)["RecordingTranscript"] == frozen, "source mutation cannot alter archive");
        reread.Description = "{}";
        Check(archive.TryGetMaterial("clip:1", out var again) && again.Description != "{}", "returned objects cannot mutate archive");
        Check(archive.CandidateReviewFacts(candidate)["accepted_input_evidence"].Count() == 2, "input-event snapshot immutable");
        Check(!archive.TrySelectCandidates(new[] { "invented" }, out _, out _), "unknown key rejected");
        Check(!archive.TrySelectCandidates(new[] { "clip:1" }, new[] { "input:999" }, out _, out _), "unknown introduction rejected");
        Check(!archive.TrySelectCandidates(new string[0], out _, out _), "empty selection is unresolved, not permission");
        Check(!archive.TrySelectCandidates(Enumerable.Range(1, 17).Select(i => "clip:" + i), out _, out _), "explicit candidate limit");
        Check(archive.TryReadRetrieval(Decision(archive, "select", new[] { "clip:1", "clip:80" }).ToString(), out _, out _), "structured model retrieval accepted");
        var wrong = Decision(archive, "select", new[] { "clip:1" }); wrong["snapshot_id"] = "old";
        Check(!archive.TryReadRetrieval(wrong.ToString(), out _, out _), "cross-request retrieval fenced");
        wrong = Decision(archive, "select", new[] { "invented" });
        Check(!archive.TryReadRetrieval(wrong.ToString(), out _, out _), "retrieval hallucinated key rejected before IR");
        wrong = Decision(archive, "index"); wrong["page"] = 0.5;
        Check(!archive.TryReadRetrieval(wrong.ToString(), out _, out _), "fractional cursor rejected");
        Check(!archive.TryReadRetrieval(Decision(archive, "index").ToString() + "{}", out _, out _), "multiple roots rejected");
        var huge = Archive(1); var desc = JObject.Parse(huge.Materials[0].Description);
        string longText = string.Concat(Enumerable.Range(0, 12000).Select(i => "句" + i + "，"));
        desc["RecordingTranscript"] = longText; huge.Materials[0].Description = desc.ToString(Formatting.None);
        var largeArchive = RecordingArchiveSnapshot.Create(huge); var joined = new StringBuilder(); int pages = 1;
        for (int page = 0; page < pages; page++)
        {
            Check(largeArchive.TryEvidencePage("clip:1", "", page, out var facts), "long original exact evidence page available");
            pages = (int)facts["page_count"]; Check((int)facts["offset"] == joined.Length, "evidence offsets contiguous"); joined.Append((string)facts["json_slice"]);
        }
        Check((string)JObject.Parse(joined.ToString())["RecordingTranscript"] == longText, "long transcript fully recoverable without silent slicing loss");
        Check(!largeArchive.TrySelectCandidates(new[] { "clip:1" }, out _, out error) && error == "archive-selected-evidence-requires-smaller-scope", "oversized full evidence yields explicit missing scope, never partial executable candidate");
        Check(!largeArchive.TryEvidencePage("clip:1", "input:0", 0, out _), "ambiguous source/event selection rejected");
        Check(archive.TryEvidencePage("", "input:79", 0, out var introduction) && ((string)introduction["json_slice"]).Contains("这次纠正"), "ordinary correction can be expanded exactly");
        Check((string)archive.RetrievalFacts(candidate, archive.IndexPage(0), null, 4)["current_intent"]["personaEvidence"] == "角色明确选择的完整原文", "retrieval preserves complete protected persona evidence");
        var retrievalSchema = (JObject)archive.RetrievalFormat()["json_schema"]["schema"]["properties"];
        Check(retrievalSchema["keys"]["items"]["enum"].Values<string>().Contains("clip:1") &&
            !retrievalSchema["event_ids"]["items"]["enum"].Values<string>().Contains("clip:1") &&
            retrievalSchema["event_ids"]["items"]["enum"].Values<string>().Contains("input:0"), "schema prevents actual-model clip-as-event mistake");
        wrong = Decision(archive, "index"); wrong["event_ids"] = new JArray("clip:1");
        Check(!archive.TryReadRetrieval(wrong.ToString(), out _, out error) && error.Contains("event-ids"), "wrong identifier domain gives bounded-repair fact");
        wrong = Decision(archive, "select", new[] { "clip:1" }); wrong["key"] = "clip:1";
        Check(!archive.TryReadRetrieval(wrong.ToString(), out _, out _), "non-evidence operation leaves scalar lookup keys empty");
        candidate.ArchiveExpansionRequested = true;
        candidate.ArchiveExpansionRequest = new JObject { ["decision"] = "handoff", ["resolution"] = "source_expansion", ["selection_evidence"] = new JArray(new JObject { ["key"] = "clip:80", ["quote"] = "" }) };
        candidate.ArchiveVisitedPages.Add(new JObject { ["kind"] = "index", ["page"] = 0 });
        var expansion = archive.RetrievalFacts(candidate, archive.IndexPage(0), null, 7);
        Check((bool)expansion["expansion_requested"] && JToken.DeepEquals(expansion["expansion_request"], candidate.ArchiveExpansionRequest)
            && expansion["visited_pages"].Count() == 1, "IR expansion request and cumulative page progress reach next retrieval as facts");
        candidate.ArchiveExpansionRequest["resolution"] = "changed-outside"; candidate.ArchiveVisitedPages.Clear();
        Check((string)expansion["expansion_request"]["resolution"] == "source_expansion" && expansion["visited_pages"].Count() == 1,
            "retrieval expansion facts are detached, not a mutable execution command");
        var searchOriginal = Archive(120); var searchArchive = RecordingArchiveSnapshot.Create(searchOriginal);
        var correctionMatches = searchArchive.SearchIndexPage("纠正", 0);
        Check((string)correctionMatches["detail"] == "index_only" && (int)correctionMatches["total_matches"] == 2 &&
            correctionMatches["entries"].Any(e => (string)e["key"] == "clip:80") &&
            correctionMatches["entries"].Any(e => (string)e["event_id"] == "input:79"), "model literal query finds late correction source and accepted introduction, not first-page ordinal guess");
        var earlyMatches = searchArchive.SearchIndexPage("第一段", 0);
        Check(earlyMatches["entries"].Any(e => (string)e["key"] == "clip:1"), "same lookup can locate old low-score source");
        var many = new JArray(); var firstSearchPage = searchArchive.SearchIndexPage("普通讲话", 0);
        for (int page = 0; page < (int)firstSearchPage["page_count"]; page++)
            foreach (var entry in searchArchive.SearchIndexPage("普通讲话", page)["entries"]) many.Add(entry.DeepClone());
        Check(many.Count == 236 && many.OfType<JObject>().Where(e => (string)e["kind"] == "recording").Select(e => (string)e["key"]).Distinct().Count() == 118,
            "literal results paginate all matching old/new originals without latest-N clipping");
        Check(searchArchive.SearchIndexPage("普通讲话", (int)firstSearchPage["page_count"]) == null, "search cursor cannot exceed known pages");
        Check((int)searchArchive.SearchIndexPage(".*", 0)["total_matches"] == 0, "query is literal, not regex or semantic routing");
        Check((int)searchArchive.SearchIndexPage("not actually present", 0)["total_matches"] == 0 && searchArchive.MaterialCount == 120,
            "empty literal result retains archive and is not a missing-source judgment");
        Check(searchArchive.SearchIndexPage("", 0) == null && searchArchive.SearchIndexPage(new string('字', 65), 0) == null,
            "search text bounds explicit");
        var middle = Archive(1); var middleDescription = JObject.Parse(middle.Materials[0].Description);
        middleDescription["RecordingTranscript"] = new string('前', 200) + "Needle.[literal]" + new string('后', 200);
        middle.Materials[0].Description = middleDescription.ToString(Formatting.None);
        var middleArchive = RecordingArchiveSnapshot.Create(middle); var middleHit = middleArchive.SearchIndexPage("needle.[literal]", 0);
        Check((int)middleHit["total_matches"] == 1 && (int)middleHit["entries"][0]["literal_matches"][0]["offset"] == 200,
            "search reads full original middle beyond head/tail index excerpts and preserves true offset");
        Check(middleArchive.TrySelectCandidates(new[] { "clip:1" }, out var middleCandidate, out _) &&
            (string)JObject.Parse(middleCandidate.Materials[0].Description)["RecordingTranscript"] == (string)middleDescription["RecordingTranscript"],
            "search-index location never replaces full candidate evidence");
        var searchDecision = Decision(searchArchive, "search"); searchDecision["query"] = "纠正";
        Check(searchArchive.TryReadRetrieval(searchDecision.ToString(), out _, out _), "typed model-authored literal search accepted");
        searchDecision["query"] = "";
        Check(!searchArchive.TryReadRetrieval(searchDecision.ToString(), out _, out _), "empty search rejected for bounded correction");
        searchDecision = Decision(searchArchive, "select", new[] { "clip:1" }); searchDecision["query"] = "纠正";
        Check(!searchArchive.TryReadRetrieval(searchDecision.ToString(), out _, out _), "selection cannot smuggle a separate search operation");
        Check(searchArchive.SnapshotId == RecordingArchiveSnapshot.Create(searchOriginal).SnapshotId, "read-only lookup does not mutate original evidence or snapshot identity");
        var small = Archive(2);
        var smallArchive = RecordingArchiveSnapshot.Create(small);
        Check(smallArchive.TrySelectCandidates(smallArchive.MaterialKeys, smallArchive.InputEventIds, out var smallCandidate, out _)
            && smallCandidate.ExpandedArchiveInputEvents.Count == 2, "small direct path keeps all accepted introductions");
        small.ArchiveInputEvents = Archive(120).ArchiveInputEvents;
        Check(RecordingArchiveSnapshot.NeedsRetrieval(small), "long accepted history needs retrieval even with only two clips");
        var empty = Archive(0); empty.ArchiveInputEvents.Add(new JObject { ["text"] = "介绍尚未开始的新歌" });
        var emptyArchive = RecordingArchiveSnapshot.Create(empty);
        Check(emptyArchive.TrySelectCandidates(emptyArchive.MaterialKeys, emptyArchive.InputEventIds, out var emptyCandidate, out _)
            && emptyCandidate.ExpandedArchiveInputEvents.Count == 1, "zero-clip direct path still preserves accepted introduction");
        wrong = Decision(archive, "index"); wrong["snapshot_id"] = new JObject();
        Check(!archive.TryReadRetrieval(wrong.ToString(), out _, out _), "non-string snapshot cannot throw or bypass boundary");
        var thirteen = Archive(13);
        thirteen.Materials[0].LastCompletedPlayback = new JObject { ["sequence"] = 1, ["position"] = 1, ["segmentCount"] = 1, ["revision"] = 2 };
        thirteen.Materials[7].LastCompletedPlayback = new JObject { ["sequence"] = 2, ["position"] = 1, ["segmentCount"] = 1, ["revision"] = 1 };
        thirteen.Materials[11].LastCompletedPlayback = new JObject { ["sequence"] = 3, ["position"] = 1, ["segmentCount"] = 1, ["revision"] = 1 };
        var compactArchive = RecordingArchiveSnapshot.Create(thirteen);
        var compact = compactArchive.InitialIndexPage();
        Check((string)compact["kind"] == "complete-directory" && (bool)compact["complete_archive"] && compact["entries"].Count() == 26,
            "13-source request shows every recording and introduction in one directory, without semantic preselection");
        Check(compact["entries"].Any(e => (string)e["key"] == "clip:13") && compact["entries"].Any(e => (string)e["event_id"] == "input:12"),
            "complete directory includes the final entries, not only first page");
        var played = compactArchive.CompletedPlaybackIndex();
        Check(played["entries"].Count() == 3 && (int)played["entries"][2]["last_completed_playback"]["sequence"] == 3,
            "actual completed playback identity and order visible to retriever");
        Check((string)compact["entries"][0]["accepted_input_event_ids"][0] == "input:0", "recordings directly name related accepted input evidence");
        thirteen.Materials[0].LastCompletedPlayback["sequence"] = 999;
        Check((int)compactArchive.CompletedPlaybackIndex()["entries"][0]["last_completed_playback"]["sequence"] == 1,
            "playback index is frozen with source snapshot");
        played["entries"][0]["last_completed_playback"]["sequence"] = 777;
        Check((int)compactArchive.CompletedPlaybackIndex()["entries"][0]["last_completed_playback"]["sequence"] == 1,
            "returned playback facts cannot mutate frozen index");
        Check((string)searchArchive.InitialIndexPage()["kind"] == "index" && !(bool)searchArchive.InitialIndexPage()["complete_archive"],
            "120 recordings retain explicit paged directory rather than falsely claiming all shown");
        Check(compactArchive.TrySelectCandidates(new[] { "clip:1", "clip:8", "clip:12" }, new[] { "input:0", "input:7", "input:11" }, out var three, out _)
            && three.Materials.Count == 3 && three.ExpandedArchiveInputEvents.Count == 3, "one model selection batch expands three complete source/event facts");
        Check(!compactArchive.AddsUnexpandedEvidence(three, new[] { "clip:12", "clip:1", "clip:8" }, new[] { "input:11", "input:0", "input:7" }),
            "reordered identical selection is not new evidence");
        Check(!compactArchive.AddsUnexpandedEvidence(three, new[] { "clip:1" }, new[] { "input:0" }), "subset is not source expansion");
        Check(compactArchive.AddsUnexpandedEvidence(three, new[] { "clip:1", "clip:2" }, new[] { "input:0" }), "genuinely unexpanded recording allowed");
        Check(compactArchive.AddsUnexpandedEvidence(three, new[] { "clip:1" }, new[] { "input:0", "input:1" }), "new introduction alone counts as evidence progress");
        Check(compactArchive.AddsUnexpandedEvidence(thirteen, new[] { "clip:1" }, new[] { "input:0" }), "initial selection does not require artificial expansion");
        var observed = new JArray();
        compactArchive.RecordObservedPage(observed, compact);
        compactArchive.RecordObservedPage(observed, compact);
        Check(observed.Count == 1 && compactArchive.HasObservedPage(observed, compact), "identical directory view is cached once");
        Check(compactArchive.HasObservedPage(observed, compactArchive.IndexPage(0)), "complete directory already includes ordinary index page zero");
        Check(!searchArchive.HasObservedPage(observed, searchArchive.IndexPage(0)), "page cache cannot cross snapshot identity");
        var firstHit = searchArchive.SearchIndexPage("第一段", 0);
        searchArchive.RecordObservedPage(observed, firstHit);
        Check(searchArchive.HasObservedPage(observed, searchArchive.SearchIndexPage("第一段", 0)) &&
            !searchArchive.HasObservedPage(observed, searchArchive.SearchIndexPage("纠正", 0)), "same literal search cached while a new query remains available");
        Check(RecordingArchiveSnapshot.RetrievalContract.Contains("单个连续原文字面片段") &&
            RecordingArchiveSnapshot.RetrievalContract.Contains("足够定位就直接select"), "retrieval contract permits direct selection and explains literal multiword semantics");
        var unknownIdentity = Archive(1);
        unknownIdentity.Materials[0].ClipRef = null; unknownIdentity.Materials[0].CaptureSerial = 0;
        unknownIdentity.ArchiveInputEvents[0]["clip"] = null; unknownIdentity.ArchiveInputEvents[0]["capture"] = 0;
        Check(RecordingArchiveSnapshot.Create(unknownIdentity).InitialIndexPage()["entries"][0]["accepted_input_event_ids"].Count() == 0,
            "unknown/null identity cannot invent a source-to-accepted-input link");
        compactArchive.RecordExpandedCandidates(three);
        compactArchive.RecordExpandedCandidates(three);
        Check(three.ArchiveVisitedPages.Count == 1, "expanded candidate evidence recorded once");
        Check(compactArchive.TrySelectCandidates(new[] { "clip:2" }, new[] { "input:1" }, out var otherSet, out _), "alternate candidate set available");
        otherSet.ArchiveVisitedPages = (JArray)three.ArchiveVisitedPages.DeepClone();
        compactArchive.RecordExpandedCandidates(otherSet);
        Check(!compactArchive.AddsUnexpandedEvidence(otherSet, new[] { "clip:1", "clip:8", "clip:12" }, new[] { "input:0", "input:7", "input:11" }),
            "A-to-B-to-A source expansion cycle cannot count old facts as new evidence");
        Check(compactArchive.ExpandedKeys(otherSet).Length == 4 && compactArchive.ExpandedEvents(otherSet).Length == 4,
            "cumulative expanded identity available across candidate replacements");
        Check(compactArchive.AddsUnexpandedEvidence(otherSet, new[] { "clip:3" }, new[] { "input:2" }), "new evidence still available after cycle guard");
        return count;
    }
}
