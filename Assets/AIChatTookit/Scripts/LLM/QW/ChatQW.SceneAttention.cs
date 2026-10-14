using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    private static bool AdmitSceneSpeechWire(string json, out JObject diagnostic)
    {
        diagnostic = MeasureRoomDecisionBudget(json, RoomDecisionBudget);
        // Formal replies have their own output contract (up to 768 tokens), so
        // use its real reserve rather than the decision-only 640-token check.
        bool allowed = (bool)diagnostic["textWithinBudget"] && (bool)diagnostic["totalWithinBudget"] &&
            (int)diagnostic["imageCount"] <= 5 && (int)diagnostic["wireUtf8Bytes"] <= RoomDecisionMaximumWireBytes;
        diagnostic["allowed"] = allowed;
        return allowed;
    }

    private const int SceneAttentionSlots = 12, SceneAttentionDetailUnits = 3600;
    private const string SceneAttentionMarker = "[Scene attention v1]";
    private const string CognitionMarker = "[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]";
    private sealed class AttentionCollection
    {
        public JArray rows;
        public bool currentPage;
    }

    // Materialize the persistent runtime attention for this consumer. Legacy
    // callers without a runtime snapshot keep bounded request-local selection.
    // Offered pages, active bindings and unknown constraints remain protected.
    private static void ApplySceneAttention(IEnumerable<JObject> roots, int slots = SceneAttentionSlots,
        int units = SceneAttentionDetailUnits, bool reply = false)
    {
        var documents = roots.ToArray();
        var state = documents.SelectMany(r => r.DescendantsAndSelf().OfType<JObject>())
            .FirstOrDefault(r => (string)r["source"] == "runtime-attention-state-v1" && (bool?)r["actionAuthorization"] == false);
        var attended = new HashSet<string>(StringComparer.Ordinal);
        if (state != null)
        {
            slots = Math.Min(slots, Math.Max(1, Math.Min(12, (int?)state["ordinarySlots"] ?? slots)));
            units = Math.Min(units, Math.Max(0, Math.Min(6500, (int?)state["optionalDetailUnits"] ?? units)));
            if (state["selectedEntities"] is JArray entries)
                foreach (JObject entry in entries.OfType<JObject>())
                    if (!string.IsNullOrEmpty((string)entry["entityId"])) attended.Add((string)entry["entityId"]);
        }
        string replyAction = documents.Select(r => (string)r.SelectToken("currentActionReceipt.requestedActionId"))
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        string replyNeed = documents.Select(r => (string)r["replyEvidenceNeed"]).FirstOrDefault(v => v != null);
        var collections = new List<AttentionCollection>();
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        var referred = new HashSet<string>(StringComparer.Ordinal);
        var query = new StringBuilder();
        foreach (var root in documents)
        {
            foreach (string field in new[] { "currentUserText", "latestUserText", "requestedUserText" })
                query.Append((string)root[field]).Append(' ');
            query.Append((string)root.SelectToken("humanInput.primaryUtterance"));
            foreach (string field in new[] { "spatialGoal", "standingGoal", "pendingDeviceRequest", "pendingObservationGoal" })
            {
                var goal = root[field] as JObject;
                if (goal == null) continue;
                string status = (string)goal["status"];
                CollectRoomWorkingMemoryActiveBinding(goal, status == "satisfied" || status == "cancelled" ? referred : pinned);
            }
            CollectRoomDecisionEntityReferences(root.SelectToken("workingMemory.completedTasks"), referred);
            foreach (var node in root.DescendantsAndSelf().OfType<JObject>().ToArray())
            {
                if (reply && node["objectContext"] is JObject objectContext)
                {
                    // A previous selected lamp must not remain permanently pinned
                    // while the current reply reports walking to the user. It is
                    // still a recall candidate for questions/references to it.
                    bool currentAction = !string.IsNullOrEmpty(replyAction) &&
                        (string)node.SelectToken("lastAction.actionId") == replyAction;
                    foreach (string field in new[] { "selected", "stateResult", "targetReview" })
                        CollectRoomDecisionEntityReferences(objectContext[field], currentAction ? pinned : referred);
                    foreach (string field in new[] { "knownObjectHistory", "recentObservations" })
                        if (objectContext[field] is JArray records) collections.Add(new AttentionCollection { rows = records });
                }
                if (node["currentCaptureId"] != null && node["currentObjects"] is JArray current && node["historicalObjects"] is JArray history)
                { collections.Add(new AttentionCollection { rows = current }); collections.Add(new AttentionCollection { rows = history }); }
                foreach (string field in new[] { "visibleObjectCatalog", "sceneSpatialMemory" })
                {
                    if (!(node[field] is JObject context)) continue;
                    if (field == "visibleObjectCatalog")
                    {
                        if (context.SelectToken("current.candidates") is JArray page)
                            collections.Add(new AttentionCollection { rows = page, currentPage = true });
                        if (context["memory"] is JArray memory) collections.Add(new AttentionCollection { rows = memory });
                    }
                    else if (context["objects"] is JArray objects) collections.Add(new AttentionCollection { rows = objects });
                }
            }
        }
        collections = collections.GroupBy(c => c.rows).Select(group => group.First()).ToList();
        var all = collections.SelectMany(c => c.rows.OfType<JObject>()).ToArray();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in collections)
            foreach (var row in collection.rows.OfType<JObject>())
                if (collection.currentPage || pinned.Contains((string)row["entityId"] ?? "") || !SceneAttentionOptional(row))
                    selected.Add(RoomWorkingMemoryObjectKey(row));
        var terms = SceneAttentionTerms(query.ToString());
        int remaining = Math.Max(0, slots - selected.Count), spent = 0;
        foreach (var group in all.Where(SceneAttentionOptional).GroupBy(RoomWorkingMemoryObjectKey)
            .Where(group => !selected.Contains(group.Key))
            .OrderByDescending(group => group.Max(row => SceneAttentionRank(row, terms, referred) +
                (attended.Contains((string)row["entityId"] ?? "") ? 40 : 0) +
                (replyNeed == "current-visual" && (bool?)row["currentOpaquePixels"] == true ? 25 : 0) +
                (replyNeed == "memory-or-direction" && (bool?)row["historical"] == true ? 25 : 0)))
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            // Count all copies against one common detail budget. Exact sharing
            // may subsequently reduce them, but is not needed to make this bound.
            long cost = group.Sum(row => EstimateRoomDecisionTextUnits(row.ToString(Formatting.None)));
            if (remaining <= 0 || cost > units - spent) continue;
            selected.Add(group.Key); remaining--; spent += (int)cost;
        }
        foreach (var collection in collections)
        {
            if (collection.currentPage) continue;
            int before = collection.rows.Count;
            var keep = collection.rows.Where(row => !(row is JObject obj) || !SceneAttentionOptional(obj) ||
                selected.Contains(RoomWorkingMemoryObjectKey(obj))).Select(row => row.DeepClone()).ToArray();
            if (keep.Length == before) continue;
            var parent = collection.rows.Parent as JProperty;
            collection.rows.Replace(new JArray(keep));
            if (parent?.Parent is JObject owner)
            {
                if (parent.Name == "currentObjects") owner["currentContextTruncated"] = true;
                if (parent.Name == "historicalObjects") owner["historicalContextTruncated"] = true;
                owner["attentionOmission"] = new JObject {
                    ["scope"] = "Request-only selection of observed objects. Omitted records remain in runtime and may be recalled/reobserved; omission is not absence, invisibility, forgotten identity or new action authority.",
                    ["ordinarySlots"] = slots, ["optionalDetailUnits"] = units
                };
            }
        }
    }

    private static bool SceneAttentionOptional(JObject row)
    {
        if (string.IsNullOrEmpty((string)row["entityId"]) ||
            string.IsNullOrEmpty((string)row["sourceCaptureId"] ?? (string)row["captureId"])) return false;
        var cognition = new HashSet<string>(new[] { "bearing", "currentOpaquePixels", "cameraYawDegreesRight", "cameraElevationDegreesUp",
            "cameraDistanceMeters", "bodyYawDegreesRight", "bodyHorizontalDistanceMeters" }, StringComparer.Ordinal);
        if (row.Descendants().OfType<JProperty>().Any(p => !RoomDecisionKnownObservationFields.Contains(p.Name) &&
            !RoomDecisionKnownObservationChildFields.Contains(p.Name) && !cognition.Contains(p.Name))) return false;
        return !row.Descendants().OfType<JProperty>().Any(p => p.Name == "actionAuthorization" && p.Value.Type == JTokenType.Boolean && (bool)p.Value);
    }

    private static HashSet<string> SceneAttentionTerms(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        var terms = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < text.Length; ++i)
            if (char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i])) terms.Add(text.Substring(i - 1, 2));
        return terms;
    }

    private static double SceneAttentionRank(JObject row, HashSet<string> query, HashSet<string> referred)
    {
        var hint = row["authoredHint"] as JObject;
        string names = (string)row["modelDescription"] + " " + (string)row["sceneLabel"] + " " +
            (string)hint?["sceneLabel"] + " " + (string)hint?["rendererName"];
        // Lexical overlap is a retrieval weight, never semantic identification.
        double rank = SceneAttentionTerms(names).Count(query.Contains) * 10;
        if (referred.Contains((string)row["entityId"] ?? "")) rank += 30;
        if (!string.IsNullOrEmpty((string)row["matchedOriginalTarget"])) rank += 20;
        if (!string.IsNullOrEmpty((string)row["modelDescription"])) rank += 5;
        return rank + RoomWorkingMemoryRecallStrength(row);
    }

    private static string ApplySceneAttentionToInput(string input)
    {
        try
        {
            var root = ReadRoomTaskRecoveryJson(input);
            if (root["visualEvidence"]?.Type == JTokenType.String)
                root["visualEvidence"] = ProjectSceneAttentionText((string)root["visualEvidence"],
                    (string)root["requestedUserText"] ?? (string)root["currentUserText"], root);
            else ApplySceneAttention(new[] { root });
            return root.ToString(Formatting.None);
        }
        catch (JsonException) { return input; }
    }

    private static string ProjectSceneAttentionText(string text, string utterance = null, JObject input = null)
    {
        if (string.IsNullOrEmpty(text) || text.Contains(SceneAttentionMarker) || text.Contains("[Reply fact packet v1]") || text.Contains("[Scene fact sharing v1]")) return text;
        // Parse only typed, producer-owned blocks at line boundaries. Human text
        // and embedded JSON strings are never recursively treated as instructions.
        string[] markers = { CognitionMarker, "[视觉关注与行动事实]",
            "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]", "[Bound facts for this public reply]", "[只读场景问答]" };
        var blocks = new List<Tuple<int, int, JObject>>();
        int bound = text.IndexOf("(用户刚开口讲了下面这段话，请回应)", StringComparison.Ordinal);
        if (bound < 0) bound = text.Length;
        for (int line = 0; line < bound;)
        {
            int next = text.IndexOf('\n', line);
            foreach (string marker in markers)
            {
                if (line + marker.Length > bound || string.CompareOrdinal(text, line, marker, 0, marker.Length) != 0) continue;
                int start = line + marker.Length;
                while (start < bound && char.IsWhiteSpace(text[start])) start++;
                if (start >= bound || text[start] != '{') continue;
                int depth = 0, end = start; bool quote = false, escape = false;
                for (; end < bound; end++)
                {
                    char c = text[end];
                    if (quote) { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == '"') quote = false; continue; }
                    if (c == '"') quote = true;
                    else if (c == '{' || c == '[') depth++;
                    else if ((c == '}' || c == ']') && --depth == 0) { end++; break; }
                }
                if (depth != 0) continue;
                try { blocks.Add(Tuple.Create(start, end, ReadRoomTaskRecoveryJson(text.Substring(start, end - start)))); }
                catch (JsonException) { }
                next = text.IndexOf('\n', end); break;
            }
            line = next < 0 ? bound : next + 1;
        }
        if (blocks.Count == 0) { if (input != null) ApplySceneAttention(new[] { input }); return text; }
        var roots = blocks.Select(block => block.Item3).ToList();
        if (input != null) roots.Add(input);
        roots.Add(new JObject { ["currentUserText"] = utterance });
        ApplySceneAttention(roots);
        var output = new StringBuilder(); int cursor = 0;
        foreach (var block in blocks)
        {
            output.Append(text, cursor, block.Item1 - cursor).Append(block.Item3.ToString(Formatting.None));
            cursor = block.Item2;
        }
        output.Append(text, cursor, text.Length - cursor);
        return SceneAttentionMarker + " Observed-object attention is shared across this request; full source records remain in runtime.\n" + output;
    }
}
