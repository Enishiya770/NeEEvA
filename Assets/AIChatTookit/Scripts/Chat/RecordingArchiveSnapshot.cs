using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Immutable request-local archive. Index excerpts locate evidence; they cannot
// authorize a material, a range, a source, or a missing-recording assertion.
public sealed class RecordingArchiveSnapshot
{
    public const int IndexPageCharacters = 5500;
    public const int EvidencePageCharacters = 6000;
    public const int CompleteDirectoryCharacters = 14000;
    public const int MaximumRetrievalRequests = 16;
    public const int MaximumCandidateMaterials = 16;
    public const string RetrievalContract =
        "你是录音档案的只读检索器，不扮演角色、不选新的角色意愿、不批准来源、不执行音频。保留完整user/current_intent，根据真实事件时序找出实现整个目标所需核对的候选和替代候选。只输出schema JSON。" +
        "索引摘录不完整，普通讲话可能介绍、纠正或指代前后歌声；不要按声学分数、ready、最新N条或RecordingSequence当歌段编号筛选。先后相邻也不代表同曲。" +
        "先浏览已给目录。complete_archive=true时已列出整个档案的所有材料和接受输入，结合原文、关联输入及last_completed_playback事实，足够定位就直接select，一次提交整个目标的所有候选和介绍事件，不需要先search或逐条evidence。completed_playbacks是实际播完身份与顺序索引，不是段号或本轮许可。目录不足时才用search/index/evidence，不从编号猜段落。search的query是单个连续原文字面片段，不是自然语言指令、正则、空格分词或OR；多个短词分别查询，不能把“第一段 第二段 第三段”当成三个查询。零字面匹配不证明素材不存在。" +
        "search返回index_only匹配索引和原文附近摘录，不是来源或目标授权；按page读取全部命中页。index读取完整索引指定页，evidence读取某key或event_id的完整JSON原文页；offset/total/page_count说明还有未读内容，缺页不代表缺录音。" +
        "key/keys只能用录音key，event_id/event_ids只能用accepted_input的event_id，二者不可互换。index/search/select/unresolved时key和event_id均为空字符串；search时query为1至64字符短原文字面词，其余operation的query为空字符串；未选择介绍事件时event_ids=[]。evidence只填key或event_id其中一个。" +
        "每次响应都用keys/event_ids保留跨页需要的累积候选，working_candidates会在下一次提供。select的keys是交下一阶段核对的候选集合，不是播放顺序；包括相关续接版本、可能的替代音源及普通介绍/纠正事件event_ids。下阶段才能选择有序素材并独立审核。" +
        "索引足以定位相关候选后即可select：程序会自动加载这些keys和event_ids的完整原文、PCM与范围事实交后续IR及独立审核；检索阶段不判定是否真唱过，也不必先逐条读取完整证据。evidence分页仅用于索引不足以定位候选时；切换query仍须保留已经定位且仍相关的keys/event_ids。" +
        "多段与重复目标要保留完整范围，不能只取最近一段。already_expanded_keys/expanded_input_event_ids已经完整交给IR；重新select完全相同的集合没有新证据。source_expansion时应在目录找真正未展开的相关候选或介绍；找不到可提供的新证据则unresolved，不重复已访问页或同一集合。没有看过的页面不能断言没有对应录音；资料仍不足用unresolved并标具体missing，不编造缺失。";

    private readonly RecordingSingingDecisionContext original;
    private readonly RecordingSingingMaterial[] archive;
    private readonly JObject[] inputEvents;
    private readonly List<JArray> indexPages = new List<JArray>();
    public string SnapshotId { get; private set; }
    public int MaterialCount => archive.Length;
    public int InputEventCount => inputEvents.Length;
    public int IndexPageCount => indexPages.Count;
    public string[] MaterialKeys => archive.Select(m => m.Key).ToArray();
    public string[] InputEventIds => inputEvents.Select(e => (string)e["event_id"]).ToArray();

    private RecordingArchiveSnapshot(RecordingSingingDecisionContext source)
    {
        original = CopyContext(source);
        archive = source.Materials.Select(CopyMaterial).ToArray();
        inputEvents = (source.ArchiveInputEvents ?? new JArray()).OfType<JObject>()
            .Select((e, i) => { var copy = (JObject)e.DeepClone(); copy["event_id"] = "input:" + i; return copy; }).ToArray();
        SnapshotId = Hash(new JObject {
            ["user"] = source.UserText, ["input"] = source.CurrentInput?.DeepClone(),
            ["singingSession"] = source.SingingSession?.DeepClone(),
            ["intent"] = source.CurrentIntent?.DeepClone(), ["materials"] = new JArray(archive.Select(m => new JObject { ["key"] = m.Key, ["clip"] = m.ClipRef, ["capture"] = m.CaptureSerial, ["sequence"] = m.RecordingSequence, ["revision"] = m.Revision, ["description"] = m.Description, ["playback"] = m.PlaybackStatus, ["clean"] = m.HasCleanRange, ["expanded"] = m.HasExpandedRange, ["lastPlayback"] = m.LastCompletedPlayback?.DeepClone() })),
            ["events"] = new JArray(inputEvents.Select(e => e.DeepClone())) }.ToString(Formatting.None));
        var rows = archive.Select(IndexRow).Concat(inputEvents.Select(EventIndexRow));
        var page = new JArray();
        foreach (var row in rows)
        {
            if (page.Count > 0 && page.ToString(Formatting.None).Length + row.ToString(Formatting.None).Length > IndexPageCharacters)
            { indexPages.Add(page); page = new JArray(); }
            page.Add(row);
        }
        if (page.Count > 0 || indexPages.Count == 0) indexPages.Add(page);
    }

    public static RecordingArchiveSnapshot Create(RecordingSingingDecisionContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (context.Materials.Any(m => m == null || string.IsNullOrEmpty(m.Key)) ||
            context.Materials.GroupBy(m => m.Key, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new ArgumentException("Archive keys must be nonempty and unique.");
        return new RecordingArchiveSnapshot(context);
    }

    public static bool NeedsRetrieval(RecordingSingingDecisionContext context) =>
        context != null && !context.ArchiveCandidatesReady &&
        (context.Materials.Count > 16 || context.ArchiveInputEvents.Count > 16 ||
            context.ArchiveInputEvents.ToString(Formatting.None).Length + RecordingSingingDecisionCompiler.Prompt(context).Length > 20000);

    public JObject Directory(RecordingSingingDecisionContext context = null) => new JObject {
        ["snapshot_id"] = SnapshotId, ["retained_material_count"] = MaterialCount,
        ["accepted_input_count"] = InputEventCount, ["index_page_count"] = IndexPageCount,
        ["expanded_input_event_ids"] = context?.ExpandedArchiveInputEvents == null ? new JArray() : new JArray(context.ExpandedArchiveInputEvents.OfType<JObject>().Select(e => (string)e["event_id"])),
        ["unexpanded_accepted_input_count"] = InputEventCount - (context?.ExpandedArchiveInputEvents.Count ?? 0),
        ["expanded_keys"] = new JArray(context?.Materials.Select(m => m.Key) ?? Enumerable.Empty<string>()),
        ["unexpanded_material_count"] = MaterialCount - (context?.Materials.Count ?? 0),
        ["maximum_retrieval_requests"] = MaximumRetrievalRequests,
        ["index_pages"] = new JArray(indexPages.Select((p,i) => new JObject { ["page"] = i, ["entries"] = p.Count })),
        ["scope"] = "Immutable original archive retained. Candidate selection is model retrieval, not source/goal approval. Unexpanded entries/pages are unknown, never absent. Request more evidence before judging an unresolved source." };

    // A bounded complete directory avoids making a small archive behave like a
    // large search engine. Every row is included or this falls back to ordinary
    // complete pagination; it never chooses sources on behalf of the model.
    public JObject InitialIndexPage()
    {
        var entries = new JArray(archive.Select(IndexRow).Concat(inputEvents.Select(EventIndexRow)));
        if (entries.ToString(Formatting.None).Length > CompleteDirectoryCharacters) return IndexPage(0);
        return new JObject { ["snapshot_id"] = SnapshotId, ["kind"] = "complete-directory",
            ["page"] = 0, ["page_count"] = 1, ["complete_archive"] = true,
            ["entries"] = entries, ["detail"] = "index_only",
            ["scope"] = "All retained recording and accepted-input identities are shown. Select related candidates once; runtime expands every chosen source in full before IR and independent review." };
    }

    public JObject CompletedPlaybackIndex() => new JObject {
        ["entries"] = new JArray(archive.Where(m => m.LastCompletedPlayback != null).Select(m => new JObject {
            ["key"] = m.Key, ["revision"] = m.Revision, ["capture"] = m.CaptureSerial,
            ["accepted_input_event_ids"] = RelatedInputEventIds(m),
            ["last_completed_playback"] = m.LastCompletedPlayback.DeepClone() })),
        ["scope"] = "Actual completed playback facts for every retained source with a completion record. Neither a verse numbering scheme, a filter excluding unplayed sources, nor permission to play now." };

    public JObject PageIdentity(JObject page) => page == null || (string)page["snapshot_id"] != SnapshotId ? null : new JObject {
        ["snapshot_id"] = SnapshotId, ["kind"] = page["kind"], ["page"] = page["page"], ["key"] = page["key"],
        ["event_id"] = page["event_id"], ["query"] = page["query"] };

    public bool HasObservedPage(JArray visited, JObject page)
    {
        var identity = PageIdentity(page);
        if (identity == null) return false;
        return (visited ?? new JArray()).Any(p => JToken.DeepEquals(p, identity) ||
            (string)p["snapshot_id"] == SnapshotId && (string)p["kind"] == "complete-directory" && (string)page["kind"] == "index");
    }

    public void RecordObservedPage(JArray visited, JObject page)
    {
        if (visited != null && PageIdentity(page) != null && !HasObservedPage(visited, page)) visited.Add(PageIdentity(page));
    }

    // A repeated candidate set after source_expansion cannot yield new source
    // facts from the same immutable snapshot. Stop the loop rather than resend
    // it to IR; an explicit non-source repair remains the caller's decision.
    public bool AddsUnexpandedEvidence(RecordingSingingDecisionContext context,
        IEnumerable<string> keys, IEnumerable<string> eventIds)
    {
        if (context == null || !context.ArchiveCandidatesReady) return true;
        var expandedKeys = new HashSet<string>(ExpandedKeys(context), StringComparer.Ordinal);
        var expandedEvents = new HashSet<string>(ExpandedEvents(context), StringComparer.Ordinal);
        return (keys ?? Enumerable.Empty<string>()).Any(k => !expandedKeys.Contains(k)) ||
            (eventIds ?? Enumerable.Empty<string>()).Any(id => !expandedEvents.Contains(id));
    }

    private IEnumerable<JObject> ExpandedRecords(RecordingSingingDecisionContext context) =>
        (context?.ArchiveVisitedPages ?? new JArray()).OfType<JObject>().Where(p =>
            (string)p["snapshot_id"] == SnapshotId && (string)p["kind"] == "expanded-candidates");

    public string[] ExpandedKeys(RecordingSingingDecisionContext context) =>
        (context?.ArchiveCandidatesReady == true ? context.Materials.Select(m => m.Key) : Enumerable.Empty<string>())
        .Concat(ExpandedRecords(context).SelectMany(p => (p["keys"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()))
        .Distinct(StringComparer.Ordinal).ToArray();

    public string[] ExpandedEvents(RecordingSingingDecisionContext context) =>
        (context?.ExpandedArchiveInputEvents ?? new JArray()).OfType<JObject>().Select(e => (string)e["event_id"])
        .Concat(ExpandedRecords(context).SelectMany(p => (p["event_ids"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()))
        .Distinct(StringComparer.Ordinal).ToArray();

    public void RecordExpandedCandidates(RecordingSingingDecisionContext context)
    {
        if (context == null || !context.ArchiveCandidatesReady || !ReferenceEquals(context.ArchiveSnapshot, this)) return;
        var record = new JObject { ["snapshot_id"] = SnapshotId, ["kind"] = "expanded-candidates",
            ["keys"] = new JArray(context.Materials.Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal)),
            ["event_ids"] = new JArray(context.ExpandedArchiveInputEvents.OfType<JObject>()
                .Select(e => (string)e["event_id"]).OrderBy(k => k, StringComparer.Ordinal)) };
        if (!context.ArchiveVisitedPages.Any(p => JToken.DeepEquals(p, record))) context.ArchiveVisitedPages.Add(record);
    }

    public JObject IndexPage(int page)
    {
        if (page < 0 || page >= IndexPageCount) return null;
        return new JObject { ["snapshot_id"] = SnapshotId, ["kind"] = "index", ["page"] = page,
            ["page_count"] = IndexPageCount, ["complete_archive"] = IndexPageCount == 1,
            ["entries"] = indexPages[page].DeepClone() };
    }

    // The model supplies the literal term. This lookup never interprets song
    // ordinals, acoustic scores, recency, requested intent, or playback authority.
    public JObject SearchIndexPage(string query, int page)
    {
        if (page < 0 || !ValidSearchQuery(query)) return null;
        var rows = new List<JObject>();
        JArray Matches(JObject content, params string[] fields)
        {
            var matches = new JArray();
            foreach (string field in fields) {
                if (content[field]?.Type != JTokenType.String) continue;
                string text = (string)content[field];
                int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                int start = Math.Max(0, at - 32), end = Math.Min(text.Length, at + query.Length + 48);
                matches.Add(new JObject { ["field"] = field, ["offset"] = at, ["excerpt_offset"] = start,
                    ["excerpt"] = text.Substring(start, end - start), ["total_characters"] = text.Length,
                    ["complete"] = start == 0 && end == text.Length });
            }
            return matches;
        }
        foreach (var material in archive) {
            JObject content;
            try { content = JObject.Parse(material.Description ?? "{}"); }
            catch (JsonException) { content = new JObject { ["unstructuredEvidence"] = material.Description ?? "" }; }
            var hits = Matches(content, "RecordingTranscript", "SingingSegmentText", "Lyrics", "PrecedingSpeech", "unstructuredEvidence");
            if (hits.Count == 0) continue;
            var row = IndexRow(material); row["literal_matches"] = hits; rows.Add(row);
        }
        foreach (var input in inputEvents) {
            var hits = Matches(input, "text");
            if (hits.Count == 0) continue;
            var row = EventIndexRow(input); row["literal_matches"] = hits; rows.Add(row);
        }
        var pages = new List<JArray>(); var entries = new JArray(); int characters = 2;
        foreach (var row in rows) {
            int size = row.ToString(Formatting.None).Length;
            if (entries.Count > 0 && characters + size > IndexPageCharacters) {
                pages.Add(entries); entries = new JArray(); characters = 2;
            }
            entries.Add(row); characters += size + 1;
        }
        if (entries.Count > 0 || pages.Count == 0) pages.Add(entries);
        if (page >= pages.Count) return null;
        return new JObject { ["snapshot_id"] = SnapshotId, ["kind"] = "literal-search-index", ["query"] = query,
            ["page"] = page, ["page_count"] = pages.Count, ["total_matches"] = rows.Count,
            ["match_mode"] = "ordinal-ignore-case-literal", ["detail"] = "index_only", ["entries"] = pages[page],
            ["scope"] = "Matches inspect the complete original recording text and accepted utterances. These excerpts only locate evidence; no match proves neither absence nor semantic mismatch. Read full candidates and independently review before action." };
    }

    private static bool ValidSearchQuery(string query) => !string.IsNullOrWhiteSpace(query) && query.Length <= 64 &&
        query == query.Trim() && !query.Any(char.IsControl);

    public bool TryEvidencePage(string key, string eventId, int page, out JObject facts)
    {
        facts = null;
        if (page < 0 || string.IsNullOrEmpty(key) == string.IsNullOrEmpty(eventId)) return false;
        JToken value;
        if (!string.IsNullOrEmpty(key))
        {
            var material = archive.SingleOrDefault(m => m.Key == key);
            if (material == null) return false;
            value = FullSource(material, original.CurrentInput);
        }
        else
        {
            value = inputEvents.FirstOrDefault(e => (string)e["event_id"] == eventId);
            if (value == null) return false;
        }
        string full = value.ToString(Formatting.None);
        int count = Math.Max(1, (full.Length + EvidencePageCharacters - 1) / EvidencePageCharacters);
        if (page >= count) return false;
        int offset = page * EvidencePageCharacters;
        facts = new JObject { ["snapshot_id"] = SnapshotId, ["kind"] = "complete-evidence-json-slice",
            ["key"] = key, ["event_id"] = eventId, ["page"] = page, ["page_count"] = count,
            ["offset"] = offset, ["total_characters"] = full.Length,
            ["json_slice"] = full.Substring(offset, Math.Min(EvidencePageCharacters, full.Length - offset)),
            ["scope"] = "Exact contiguous original JSON, potentially incomplete on this page; concatenate pages in order. No field or transcript was deleted from the archive." };
        return true;
    }

    public bool TrySelectCandidates(IEnumerable<string> keys, out RecordingSingingDecisionContext candidate, out string error) =>
        TrySelectCandidates(keys, Enumerable.Empty<string>(), out candidate, out error);

    public bool TrySelectCandidates(IEnumerable<string> keys, IEnumerable<string> eventIds,
        out RecordingSingingDecisionContext candidate, out string error)
    {
        candidate = null; error = "archive-selection-invalid";
        var selected = (keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        var selectedEvents = (eventIds ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if ((selected.Length == 0 && archive.Length > 0) || selected.Length > MaximumCandidateMaterials || selectedEvents.Length > 16 ||
            selected.Any(k => !archive.Any(m => m.Key == k)) ||
            selectedEvents.Any(id => !inputEvents.Any(e => (string)e["event_id"] == id))) return false;
        var result = CopyContext(original);
        result.Materials.Clear();
        result.Materials.AddRange(archive.Where(m => selected.Contains(m.Key)).Select(CopyMaterial));
        foreach (var material in result.Materials) material.RequiresExpansion = false;
        result.ArchiveSnapshot = this; result.ArchiveCandidatesReady = true;
        result.ExpandedArchiveInputEvents = new JArray(inputEvents.Where(e => selectedEvents.Contains((string)e["event_id"])).Select(e => e.DeepClone()));
        // Full user/intent and every selected source remain exact. A huge single
        // source cannot be smuggled through via truncated text or an index row.
        if (RecordingSingingDecisionCompiler.Prompt(result).Length > RecordingSingingDecisionCompiler.MaximumProtectedPromptCharacters)
        { error = "archive-selected-evidence-requires-smaller-scope"; return false; }
        candidate = result; error = null; return true;
    }

    public JObject CandidateReviewFacts(RecordingSingingDecisionContext context)
    {
        if (context == null || !ReferenceEquals(context.ArchiveSnapshot, this)) return null;
        var selected = context.Materials.Select(m => m.Key).ToArray();
        if (selected.Any(k => !archive.Any(m => m.Key == k))) return null;
        return new JObject {
            ["archive_snapshot_id"] = SnapshotId,
            ["candidate_snapshot_id"] = Hash(SnapshotId + "|" + string.Join("|", selected) + "|" + context.ExpandedArchiveInputEvents.ToString(Formatting.None)),
            ["archive_index"] = Directory(context),
            ["singing_session"] = context.SingingSession?.DeepClone(),
            ["last_playback"] = context.LastPlaybackBinding?.DeepClone(),
            ["verified_boundary"] = context.VerifiedBoundary?.DeepClone(),
            ["boundary_obligations"] = RecordingSingingDecisionCompiler.BoundaryObligationFacts(context.BoundaryObligations),
            ["boundary_resolution_proposal"] = context.BoundaryResolution?.DeepClone(),
            ["candidate_recording_facts"] = new JArray(selected.Select(k => FullSource(archive.Single(m => m.Key == k), original.CurrentInput))),
            ["accepted_input_evidence"] = context.ExpandedArchiveInputEvents.DeepClone(),
            ["continuationCoverage"] = RecordingSingingDecisionCompiler.ContinuationCoverageFacts(context),
            ["candidate_count"] = selected.Length,
            ["candidate_scope"] = "Same immutable candidate facts supplied to IR. Not only proposed refs. Other retained recordings remain unexpanded; request a further candidate before treating an unexpanded source as absent." };
    }

    // A source explicitly selected by the current accepted semantic operation is
    // already localized. Expand its complete evidence once, without asking an
    // archive search to choose a competing source again. Approval stays separate.
    public bool TrySessionCandidates(RecordingSingingDecisionContext context,
        out RecordingSingingDecisionContext candidate, out string reason)
    {
        candidate = null; reason = null;
        if (context == null || context.ArchiveExpansionRequested || context.ArchiveCandidatesReady ||
            !(context.SingingSession?["binding"] is JObject binding) || (bool?)binding["explicitRequest"] != true) return false;
        var input = binding["input"] as JObject;
        if (input == null || context.CurrentInput == null ||
            new[] { "turn", "capture", "clip" }.Any(k => !JToken.DeepEquals(context.CurrentInput[k], input[k])) ||
            (long?)binding["sessionRevision"] != (long?)context.SingingSession["revision"])
        { reason = "session-binding-owner-mismatch"; return false; }
        string clip = (string)binding["clip"];
        var material = archive.SingleOrDefault(m => m.ClipRef == clip && m.CaptureSerial == (int?)binding["capture"]);
        if (material == null) { reason = "session-bound-recording-unavailable"; return false; }
        if ((context.SingingSession["takes"] as JArray)?.OfType<JObject>().Any(t =>
            (string)t["clip"] == clip && (string)t["status"] == "deleted") == true)
        { reason = "session-bound-recording-deleted"; return false; }
        var related = new HashSet<string>(RelatedInputEventIds(material).Values<string>(), StringComparer.Ordinal);
        foreach (var e in inputEvents)
            if (JToken.DeepEquals(e["turn"], context.CurrentInput["turn"])) related.Add((string)e["event_id"]);
        if (!TrySelectCandidates(new[] { material.Key }, related.Take(16), out candidate, out reason)) return false;
        return true;
    }

    public JObject RetrievalFacts(RecordingSingingDecisionContext context, JObject page, string lastError, int remaining) => new JObject {
        ["user"] = context.UserText ?? "", ["current_input"] = context.CurrentInput?.DeepClone(),
        ["singing_session"] = context.SingingSession?.DeepClone(),
        ["current_intent"] = context.CurrentIntent?.DeepClone(), ["reviewRecovery"] = RecoveryFact(context.Facts),
        ["archive"] = Directory(context.ArchiveCandidatesReady ? context : null),
        ["completed_playbacks"] = CompletedPlaybackIndex(),
        ["page"] = page?.DeepClone(), ["remaining_requests"] = remaining,
        ["last_error"] = lastError, ["expansion_requested"] = context.ArchiveExpansionRequested,
        ["expansion_request"] = context.ArchiveExpansionRequest?.DeepClone(),
        ["expansion_scope"] = "IR request to inspect more original evidence, not source approval or execution permission.",
        ["visited_pages"] = context.ArchiveVisitedPages.DeepClone(), ["already_expanded_keys"] = new JArray(ExpandedKeys(context)),
        ["already_expanded_event_ids"] = new JArray(ExpandedEvents(context)) };

    public JObject RetrievalFormat()
    {
        JObject Enum(System.Collections.Generic.IEnumerable<string> values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        JObject List(int count, System.Collections.Generic.IEnumerable<string> values) => new JObject { ["type"] = "array", ["maxItems"] = count, ["items"] = Enum(values) };
        return new JObject { ["type"] = "json_schema", ["json_schema"] = new JObject {
            ["name"] = "recording_archive_retrieval_v1", ["strict"] = true,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject {
                    ["snapshot_id"] = new JObject { ["type"] = "string", ["enum"] = new JArray(SnapshotId) },
                    ["operation"] = new JObject { ["type"] = "string", ["enum"] = new JArray("index", "search", "evidence", "select", "unresolved") },
                    ["page"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["query"] = new JObject { ["type"] = "string", ["maxLength"] = 64 },
                    ["key"] = Enum(new[] { "" }.Concat(MaterialKeys)), ["event_id"] = Enum(new[] { "" }.Concat(InputEventIds)), ["keys"] = List(MaximumCandidateMaterials, MaterialKeys),
                    ["event_ids"] = List(16, InputEventIds), ["missing"] = new JObject { ["type"] = "string", ["maxLength"] = 160 } },
                ["required"] = new JArray("snapshot_id", "operation", "page", "query", "key", "event_id", "keys", "event_ids", "missing") } } };
    }

    public bool TryReadRetrieval(string raw, out JObject decision, out string error)
    {
        decision = null; error = "archive-retrieval-invalid";
        try {
            using (var reader = new JsonTextReader(new System.IO.StringReader(raw ?? "")) { MaxDepth = 4 })
            {
                var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read() || value.Count != 9 || value["snapshot_id"]?.Type != JTokenType.String || value["operation"]?.Type != JTokenType.String || (string)value["snapshot_id"] != SnapshotId ||
                    !new[] { "index", "search", "evidence", "select", "unresolved" }.Contains((string)value["operation"]) ||
                    value["page"]?.Type != JTokenType.Integer || (long)value["page"] < 0 || (long)value["page"] > int.MaxValue ||
                    value["key"]?.Type != JTokenType.String || value["event_id"]?.Type != JTokenType.String ||
                    value["query"]?.Type != JTokenType.String || ((string)value["query"]).Length > 64 ||
                    value["missing"]?.Type != JTokenType.String || ((string)value["missing"]).Length > 160 ||
                    !(value["keys"] is JArray keys) || keys.Count > MaximumCandidateMaterials || keys.Any(v => v.Type != JTokenType.String) ||
                    !(value["event_ids"] is JArray ids) || ids.Count > 16 || ids.Any(v => v.Type != JTokenType.String)) return false;
                if (keys.Any(k => !archive.Any(m => m.Key == (string)k))) { error = "archive-keys-must-use-known-recording-keys"; return false; }
                if (ids.Any(id => !inputEvents.Any(e => (string)e["event_id"] == (string)id))) { error = "archive-event-ids-must-use-accepted-input-event-ids-not-recording-keys"; return false; }
                string query = (string)value["query"];
                if ((string)value["operation"] == "search" ? !ValidSearchQuery(query) : query.Length != 0)
                { error = "archive-search-needs-short-literal-query-other-operations-leave-query-empty"; return false; }
                string key = (string)value["key"], eventId = (string)value["event_id"];
                if (key.Length > 0 && !ContainsKey(key) || eventId.Length > 0 && !inputEvents.Any(e => (string)e["event_id"] == eventId))
                { error = "archive-evidence-key-or-event-id-not-in-snapshot"; return false; }
                if ((string)value["operation"] != "evidence" && (key.Length > 0 || eventId.Length > 0) ||
                    (string)value["operation"] == "evidence" && (key.Length == 0) == (eventId.Length == 0))
                { error = "archive-evidence-needs-one-key-or-event-id-other-operations-leave-both-empty"; return false; }
                decision = value; error = null; return true;
            }
        } catch (JsonException) { return false; } catch (InvalidCastException) { return false; }
    }

    public JArray WorkingIndex(IEnumerable<string> keys, IEnumerable<string> eventIds)
    {
        var selected = (keys ?? Enumerable.Empty<string>()).ToArray();
        var ids = (eventIds ?? Enumerable.Empty<string>()).ToArray();
        return new JArray(archive.Where(m => selected.Contains(m.Key)).Select(IndexRow)
            .Concat(inputEvents.Where(e => ids.Contains((string)e["event_id"])).Select(EventIndexRow)));
    }

    public bool ContainsKey(string key) => archive.Any(m => m.Key == key);
    public bool TryGetMaterial(string key, out RecordingSingingMaterial material)
    { var source = archive.SingleOrDefault(m => m.Key == key); material = source == null ? null : CopyMaterial(source); return source != null; }

    private static JObject FullSource(RecordingSingingMaterial m, JObject input)
    {
        JObject value;
        try { value = JObject.Parse(m.Description ?? "{}"); }
        catch (JsonException) { value = new JObject { ["unstructuredEvidence"] = m.Description ?? "" }; }
        value["clip"] = m.ClipRef; value["key"] = m.Key; value["revision"] = m.Revision;
        value["recordingSequence"] = m.RecordingSequence; value["capture"] = m.CaptureSerial;
        value["playbackReadiness"] = m.PlaybackStatus;
        value["rangeEvidence"] = RecordingSingingDecisionCompiler.RangeEvidence(value);
        value["availableProposalRanges"] = new JArray(RecordingSingingDecisionCompiler.ProposalRanges(m));
        value["lastCompletedPlayback"] = m.LastCompletedPlayback?.DeepClone();
        value["relationToCurrentInput"] = RecordingSingingDecisionCompiler.SourceRelation(new RecordingSingingDecisionContext { CurrentInput = input }, new[] { m });
        return value;
    }

    private JObject IndexRow(RecordingSingingMaterial m)
    {
        JObject facts;
        try { facts = JObject.Parse(m.Description ?? "{}"); }
        catch (JsonException) { facts = new JObject { ["RecordingTranscript"] = m.Description ?? "" }; }
        var regions = facts["pcmLineage"]?["regions"] as JArray;
        var parents = regions == null ? new string[0] : regions.OfType<JObject>()
            .Select(r => (int?)r["sourceCaptureSerial"]).Where(c => c.HasValue && c.Value != m.CaptureSerial)
            .SelectMany(c => archive.Where(a => a.CaptureSerial == c.Value).Select(a => a.Key)).Distinct().ToArray();
        return new JObject { ["kind"] = "recording", ["key"] = m.Key, ["sequence"] = m.RecordingSequence,
            ["capture"] = m.CaptureSerial, ["revision"] = m.Revision,
            ["transcript_index"] = Excerpt((string)facts["RecordingTranscript"]),
            ["singing_index"] = Excerpt((string)facts["SingingSegmentText"]),
            ["copied_pcm_from"] = new JArray(parents),
            ["accepted_input_event_ids"] = RelatedInputEventIds(m),
            ["last_completed_playback"] = m.LastCompletedPlayback?.DeepClone(), ["detail"] = "index_only" };
    }

    private JArray RelatedInputEventIds(RecordingSingingMaterial m) => new JArray(inputEvents.Where(e =>
        !string.IsNullOrEmpty(m.ClipRef) && (string)e["clip"] == m.ClipRef ||
        m.CaptureSerial > 0 && (int?)e["capture"] == m.CaptureSerial).Select(e => (string)e["event_id"]));

    private static JObject EventIndexRow(JObject e) => new JObject {
        ["kind"] = "accepted_input", ["event_id"] = e["event_id"]?.DeepClone(),
        ["turn"] = e["turn"]?.DeepClone(), ["capture"] = e["capture"]?.DeepClone(),
        ["clip"] = e["clip"]?.DeepClone(), ["text_index"] = Excerpt((string)e["text"]), ["detail"] = "index_only" };
    private static JObject Excerpt(string text)
    {
        text = text ?? "";
        return new JObject { ["head"] = text.Substring(0, Math.Min(64, text.Length)),
            ["tail"] = text.Length > 64 ? text.Substring(Math.Max(64, text.Length - 48)) : "",
            ["characters"] = text.Length, ["complete"] = text.Length <= 112 };
    }
    private static JToken RecoveryFact(string facts)
    { try { return JObject.Parse(facts ?? "{}")["reviewRecovery"]?.DeepClone(); } catch (JsonException) { return null; } }
    private static string Hash(string text)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant(); }
    private static RecordingSingingMaterial CopyMaterial(RecordingSingingMaterial m) => new RecordingSingingMaterial {
        Key = m.Key, ClipRef = m.ClipRef, Revision = m.Revision, Description = m.Description,
        PlaybackStatus = m.PlaybackStatus, HasCleanRange = m.HasCleanRange, HasExpandedRange = m.HasExpandedRange,
        CaptureSerial = m.CaptureSerial, RecordingSequence = m.RecordingSequence,
        RequiresExpansion = m.RequiresExpansion, LastCompletedPlayback = m.LastCompletedPlayback == null ? null : (JObject)m.LastCompletedPlayback.DeepClone() };
    private static RecordingSingingDecisionContext CopyContext(RecordingSingingDecisionContext c)
    {
        var result = new RecordingSingingDecisionContext {
            UserText = c.UserText, Facts = c.Facts, RequireGroundedResolution = c.RequireGroundedResolution,
            CapabilityReviewAttempted = c.CapabilityReviewAttempted, CapabilityReviewFacts = c.CapabilityReviewFacts == null ? null : (JObject)c.CapabilityReviewFacts.DeepClone(),
            CurrentInput = c.CurrentInput == null ? null : (JObject)c.CurrentInput.DeepClone(),
            CurrentIntent = c.CurrentIntent == null ? null : (JObject)c.CurrentIntent.DeepClone(),
            SingingSession = c.SingingSession == null ? null : (JObject)c.SingingSession.DeepClone(),
            LastPlaybackBinding = c.LastPlaybackBinding == null ? null : (JObject)c.LastPlaybackBinding.DeepClone(),
            VerifiedBoundary = c.VerifiedBoundary == null ? null : (JObject)c.VerifiedBoundary.DeepClone(),
            RefineBoundary = c.RefineBoundary,
            SupportsSegmentBoundarySelection = c.SupportsSegmentBoundarySelection,
            RefineSegments = c.RefineSegments,
            BoundaryObligations = (JArray)(c.BoundaryObligations?.DeepClone() ?? new JArray()),
            BoundaryResolution = c.BoundaryResolution == null ? null : (JObject)c.BoundaryResolution.DeepClone(),
            ObserveBoundaryProposal = c.ObserveBoundaryProposal, ObserveBoundaryResult = c.ObserveBoundaryResult,
            Recovery = c.Recovery == null ? null : new RecordingSingingRecoveryBinding { InputTurn = c.Recovery.InputTurn, GoalVersion = c.Recovery.GoalVersion, RequestTurn = c.Recovery.RequestTurn, RequestQuote = c.Recovery.RequestQuote, Refs = c.Recovery.Refs, Range = c.Recovery.Range, Expected = c.Recovery.Expected, Completion = c.Recovery.Completion, Intent = c.Recovery.Intent, Music = c.Recovery.Music == null ? null : (JObject)c.Recovery.Music.DeepClone() },
            RequestEvidenceTexts = (c.RequestEvidenceTexts ?? new string[0]).ToArray(), RequestEvidence = (JArray)c.RequestEvidence.DeepClone(),
            ReplacedLegacyContracts = (c.ReplacedLegacyContracts ?? new string[0]).ToArray(),
            ArchiveInputEvents = (JArray)c.ArchiveInputEvents.DeepClone(), ExpandedArchiveInputEvents = (JArray)c.ExpandedArchiveInputEvents.DeepClone(),
            ArchiveSnapshot = c.ArchiveSnapshot, ArchiveCandidatesReady = c.ArchiveCandidatesReady,
            ArchiveExpansionRequested = c.ArchiveExpansionRequested, ArchiveRetrievalRequests = c.ArchiveRetrievalRequests,
            ArchiveExpansionRequest = c.ArchiveExpansionRequest == null ? null : (JObject)c.ArchiveExpansionRequest.DeepClone(),
            ArchiveVisitedPages = (JArray)c.ArchiveVisitedPages.DeepClone(),
            OnLegacyHandoff = c.OnLegacyHandoff, OnCandidateSnapshot = c.OnCandidateSnapshot };
        result.Materials.AddRange(c.Materials.Select(CopyMaterial)); return result;
    }
}
