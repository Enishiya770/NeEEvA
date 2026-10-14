using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatQW
{
    private string ProjectAutonomyIntentPacket(List<SendData> resident, string prompt, out JObject diagnostic)
        => ProjectAutonomyIntentWirePacket(resident, prompt, null, out diagnostic);

    private string ProjectAutonomyIntentWirePacket(List<SendData> resident, string prompt,
        Func<string, JObject> measureWire, out JObject diagnostic)
    {
        int human = prompt.IndexOf(k_FrameUserMarker, StringComparison.Ordinal);
        string tail = human < 0 ? "" : prompt.Substring(human);
        string facts = human < 0 ? prompt : prompt.Substring(0, human);
        long other = resident.Where(m => m != null).Sum(m => (long)EstimateRoomDecisionTextUnits(m.content));
        int ceiling = Math.Min(24000, Math.Max(4096, m_MaxPromptTokens));
        string projected = null; var attempts = new JArray();
        diagnostic = new JObject();
        for (int pressure = 0; pressure < 4; ++pressure)
        {
            // The final level keeps restrictions and outcomes but removes known
            // catalog/geometry detail. A private interest decision has the image;
            // selecting an executable object/route still needs the full planner.
            JObject attention = null;
            string source = pressure == 3 ? ProjectPrivateAttention(facts, true, out attention) :
                CommittedSpatialReplyProtocol.ProducerContextMarker + "\n" + facts;
            projected = ProjectReplyFacts(source, Math.Min(pressure, 2), out var fields) + tail;
            long totalText = other + EstimateRoomDecisionTextUnits(projected);
            var wireBudget = measureWire?.Invoke(projected);
            attempts.Add(new JObject { ["pressure"] = pressure, ["otherMessageTextUnits"] = other,
                ["currentPacketTextUnits"] = EstimateRoomDecisionTextUnits(projected), ["totalTextUnits"] = totalText,
                ["textCeiling"] = ceiling, ["fields"] = fields, ["attention"] = attention, ["wireBudget"] = wireBudget });
            if (wireBudget != null ? (bool?)wireBudget["allowed"] == true : totalText <= ceiling) break;
        }
        diagnostic = new JObject { ["version"] = "autonomy-joint-budget-v2", ["finalWireMeasured"] = measureWire != null, ["attempts"] = attempts,
            ["protectedHumanTailCharacters"] = tail.Length, ["engineMemoryMutated"] = false,
            ["scope"] = "All selected messages share one text budget. Full-wire admission remains authoritative. Unknown constraints and literal human input are retained even when they force rejection." };
        return projected;
    }

    private static string ProjectWorkReviewAttention(string prompt, out JObject diagnostic)
        => ProjectPrivateAttention(prompt, false, out diagnostic);

    private static string ProjectPrivateAttention(string prompt, bool interestOnly, out JObject diagnostic)
    {
        // This is a progress packet, not an object/route-selection prompt. Read
        // complete producer documents; never rewrite quoted human evidence.
        string context = CommittedSpatialReplyProtocol.ProducerContextMarker + "\n" + prompt;
        var blocks = ReadReplyFactBlocks(context, true);
        var fields = new JArray();
        var output = new StringBuilder(); int cursor = 0;
        foreach (var block in blocks)
        {
            var omitted = new JObject();
            void Omit(JObject node, string field, string path)
            {
                if (node?[field] == null) return;
                var old = node[field];
                omitted[path + field] = new JObject { ["characters"] = old.ToString(Formatting.None).Length,
                    ["items"] = old is JArray rows ? (int?)rows.Count : null };
                node.Remove(field);
            }
            void Observations(JObject node, string field, string path)
            {
                if (!(node?[field] is JArray rows)) return;
                // Future/custom restrictions inside a row are mandatory too.
                var protectedRows = rows.Where(row => !(row is JObject obj) || !SceneAttentionOptional(obj)).ToArray();
                if (protectedRows.Length == rows.Count) return;
                int count = rows.Count - protectedRows.Length;
                node[field] = new JArray(protectedRows.Select(row => row.DeepClone()));
                omitted[path + field] = new JObject { ["omittedObservedRows"] = count, ["protectedRows"] = protectedRows.Length };
            }
            void Scene(JObject scene, string path)
            {
                if (scene == null) return;
                if (scene["lastAction"] is JObject action)
                    foreach (string field in new[] { "requestedImagePoint", "navigationFocusImagePoint", "viewBefore", "viewAfter",
                        "actualViewOffset", "intentFocusWorldPoint", "trackingFocusWorldPoint", "measuredSurface", "actualRoot" })
                        Omit(action, field, path + "lastAction.");
                if (scene["objectContext"] is JObject objects)
                {
                    foreach (string field in new[] { "recentObservations", "knownObjectHistory", "verifiedObservedDeviceHistory" })
                        Observations(objects, field, path + "objectContext.");
                    if (objects["selected"] is JObject selected)
                        foreach (string field in new[] { "pointAtCapture", "localStructure" }) Omit(selected, field, path + "objectContext.selected.");
                }
                if (scene["sceneSearch"] is JObject search)
                {
                    // Preserve terminal status/reason, ownership, limits, coverage
                    // summary and the newest receipt; execution traces stay in Unity.
                    if (search["receipts"] is JArray receipts)
                        search["latestReceipt"] = receipts.LastOrDefault()?.DeepClone();
                    foreach (string field in new[] { "receipts", "candidatePageCoverage", "reviewedHistoricalCaptureIds", "revisitedMemoryEntityIds" })
                        if (search[field] is JArray) Omit(search, field, path + "sceneSearch.");
                    if (search["referenceContext"] != null && RoomWorkingMemoryKnownArchiveField("referenceContext", search["referenceContext"]))
                        Omit(search, "referenceContext", path + "sceneSearch.");
                }
            }
            if (block.marker == CognitionMarker)
                foreach (string field in new[] { "currentObjects", "historicalObjects", "views", "recentViews", "viewSamples" })
                    Observations(block.value, field, "");
            if (block.marker == "[视觉关注与行动事实]") Scene(block.value, "");
            // Known nested scene producers can occur in the task/result document.
            foreach (var property in block.value.Descendants().OfType<JProperty>().ToArray())
                if (property.Name == "sceneActionFacts" && property.Value is JObject nested) Scene(nested, property.Path + ".");
            if (omitted.Count > 0) block.value["workReviewAttention"] = new JObject {
                ["omittedDetails"] = omitted,
                ["scope"] = (interestOnly ? "Private interest/attention view. " : "Progress-only view. ") +
                    "Full user request, restrictions, ownership, result/failure receipts and unknown fields remain. Observation catalogs, geometry and older search traces stay in runtime. Omission proves neither forgetting nor success. Any next action requires fresh planning and its own authorization."
            };
            foreach (var field in block.original.Properties())
                fields.Add(new JObject { ["block"] = block.marker, ["field"] = field.Name,
                    ["before"] = RoomDecisionValueMeasure(field.Value), ["after"] = RoomDecisionValueMeasure(block.value[field.Name]) });
            output.Append(context, cursor, block.start - cursor).Append(block.value.ToString(Formatting.None));
            cursor = block.end;
        }
        output.Append(context, cursor, context.Length - cursor);
        string result = SceneFactContextCompactor.Compact(output.ToString(), out var sharing, true);
        diagnostic = new JObject { ["version"] = "work-progress-facts-v2", ["engineMemoryMutated"] = false,
            ["beforeCharacters"] = context.Length, ["afterCharacters"] = result.Length,
            ["beforeTextUnits"] = EstimateRoomDecisionTextUnits(context), ["afterTextUnits"] = EstimateRoomDecisionTextUnits(result),
            ["preservedOtherCharacters"] = context.Length - blocks.Sum(b => b.end - b.start),
            ["fields"] = fields, ["exactSharing"] = JObject.FromObject(sharing),
            ["scope"] = "Field projection followed by full-wire hard admission. Oversized mandatory text is retained and rejected locally, never silently shortened." };
        return result;
    }

    private List<SendData> CreateWorkReviewHistory(bool taskPacket)
    {
        var history = CreateScopedRoleMessages(m_DataList,
            "[Private task review behavior]\nEvaluate only the supplied current task, user corrections, actual playback and execution receipts. " +
            "Preserve authored constraints. State what remains unfinished without adding new requirements or reviving completed/cancelled work. " +
            "A proposed intent is not execution or authorization. Return only the private structured decision, never spoken dialogue or tool calls.");
        return taskPacket ? history.Where(m => m != null && m.role == "system").ToList() : history;
    }

    private bool AdmitWorkReviewWire(string json, out JObject diagnostic)
        => AdmitPrivateDecisionWire(json, "work-review", 24000, k_WorkReviewMaxTokens, out diagnostic);

    private bool AdmitAutonomyIntentWire(string json, out JObject diagnostic)
        => AdmitPrivateDecisionWire(json, "autonomy-intent", 24000, Mathf.Clamp(m_EphemeralMaxTokens, 48, 256), out diagnostic);

    private bool AdmitSceneSearchWire(string json, out JObject diagnostic)
        // Search carries the current image, request-local choice schema, pose
        // and frozen spatial graph. Leave room for that full envelope, not just
        // the debug catalog excerpt; still bound it below the room planner.
        => AdmitPrivateDecisionWire(json, "scene-search", 40000, 768, out diagnostic);

    private bool AdmitPrivateDecisionWire(string json, string kind, int ceiling, int completion, out JObject diagnostic)
    {
        try {
            int textCeiling = Math.Min(ceiling, Math.Max(4096, m_MaxPromptTokens));
            diagnostic = MeasureRoomDecisionBudget(json, textCeiling);
            long total = (long)diagnostic["estimatedUnits"];
            // Reserve the actual stage output, not the room planner's 640 tokens.
            int totalCeiling = textCeiling + 8192;
            bool allowed = (bool)diagnostic["textWithinBudget"] && total <= totalCeiling &&
                (int)diagnostic["imageCount"] <= 1 && (int)diagnostic["wireUtf8Bytes"] <= RoomDecisionMaximumWireBytes &&
                (int)diagnostic["completionReserve"] == completion;
            diagnostic["kind"] = kind;
            diagnostic["totalUnitBudget"] = totalCeiling; diagnostic["unitBudget"] = totalCeiling;
            diagnostic["totalWithinBudget"] = total <= totalCeiling; diagnostic["allowed"] = allowed;
            diagnostic["scope"] = "Private decision admission: current request, corrections, goal and receipts remain protected. Unknown/custom constraints are not silently deleted. Estimates include native schema, image, actual completion and framing; not tokenizer counts. Rejection grants no action/continuation and sends no HTTP.";
            return allowed;
        } catch (Exception) {
            diagnostic = new JObject { ["allowed"] = false, ["kind"] = kind, ["reason"] = "invalid-private-decision-payload" }; return false;
        }
    }

    private List<SendData> CreateAutonomyIntentHistory(string prompt)
    {
        var history = CreateScopedRoleMessages(CreateAcceptedAutonomyHistory(),
            "[Private autonomy review behavior] Preserve persona, current request, corrections and authored constraints. " +
            "Use the current observation to assess whether anything warrants another formal decision. " +
            "Old observations are historical, completed/cancelled tasks are not new work. Return only a private intent; never speak or execute tools.");
        // The new packet is the current observation. Apply the existing archived
        // frame policy to ALL earlier frames while retaining verbatim human words
        // and tool receipts. Unknown messages remain intact. Do not mutate history.
        var withCurrent = new List<SendData>(history) { new SendData("user", prompt) };
        var selected = new List<SendData>();
        for (int i = 0; i < history.Count; i++) selected.Add(MessageForRequest(withCurrent, i));
        return selected;
    }

    private List<SendData> CreateAcceptedAutonomyHistory()
    {
        // Reuse the already committed formal window. An auxiliary probe must
        // not resurrect tasks that formal conversation has already archived.
        int start = m_RequestHistoryAnchor == null ? 0 : m_DataList.IndexOf(m_RequestHistoryAnchor);
        if (start < 0) start = 0;
        return m_DataList.Where((message, index) => message != null && (message.role == "system" || index >= start)).ToList();
    }
}
