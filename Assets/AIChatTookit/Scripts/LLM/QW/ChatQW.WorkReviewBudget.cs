using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatQW
{
    private static string WorkReviewDiagnosticWire(string json)
    {
        var packet=JObject.Parse(json);
        foreach(var message in (packet["messages"] as JArray??new JArray()).OfType<JObject>())
            if(message["content"] is JArray parts)foreach(var part in parts.OfType<JObject>())
                if((string)part["type"]=="image_url" && part["image_url"] is JObject image) {
                    string value=(string)image["url"]??"";
                    image["url"]="[image bytes omitted from diagnostic]";
                    image["diagnosticCharacters"]=value.Length;image["diagnosticStamp"]=ActivityEvidencePolicy.Stamp(new JValue(value));
                }
        return packet.ToString(Formatting.None);
    }

    private string ProjectAutonomyIntentPacket(List<SendData> resident, string prompt, out JObject diagnostic)
        => ProjectAutonomyIntentWirePacket(resident, prompt, null, out diagnostic);

    private string ProjectAutonomyIntentWirePacket(List<SendData> resident, string prompt,
        Func<string, JObject> measureWire, out JObject diagnostic)
    {
        int human = prompt.IndexOf(k_FrameUserMarker, StringComparison.Ordinal);
        string tail = human < 0 ? "" : prompt.Substring(human);
        string facts = human < 0 ? prompt : prompt.Substring(0, human);
        // The six-field opportunity contract must also match small packets;
        // it is not an emergency budget-pressure behavior. Human tail is literal.
        facts=WorkReviewInstructionProjection.Project(facts,true);
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
            string source = CommittedSpatialReplyProtocol.ProducerContextMarker + "\n" + facts;
            projected = ProjectReplyFacts(source, Math.Min(pressure, 2), out var fields);
            // Purpose selection also applies to admitted packets with almost
            // no headroom; it is not only emergency overflow recovery.
            projected=ProjectPrivateAttention(projected,true,out attention);
            projected+=tail;
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
        prompt=WorkReviewInstructionProjection.ShareGoals(WorkReviewInstructionProjection.Project(prompt,interestOnly));
        // This is a progress packet, not an object/route-selection prompt. Read
        // complete producer documents; never rewrite quoted human evidence.
        string context = prompt.StartsWith(CommittedSpatialReplyProtocol.ProducerContextMarker+"\n",StringComparison.Ordinal)
            ? prompt : CommittedSpatialReplyProtocol.ProducerContextMarker + "\n" + prompt;
        JArray priorPool=null;
        const string poolMarker="\n[Private shared facts] ";
        int poolStart=context.IndexOf(poolMarker,StringComparison.Ordinal);
        if(poolStart>=0) {
            int poolEnd=context.IndexOf('\n',poolStart+poolMarker.Length);
            if(poolEnd<0)throw new JsonException("Unterminated private pool.");
            using(var reader=new JsonTextReader(new System.IO.StringReader(context.Substring(poolStart+poolMarker.Length,poolEnd-poolStart-poolMarker.Length)))) {
                reader.DateParseHandling=DateParseHandling.None;priorPool=JArray.Load(reader);
            }
            context=context.Remove(poolStart,poolEnd-poolStart);
        }
        // Remove only this producer's one-line reference legend before renewing it.
        context=string.Join("\n",context.Split('\n').Where(line=>!line.StartsWith("[Private fact references] ",StringComparison.Ordinal)));
        var blocks = ReadReplyFactBlocks(context, true);
        var legacyBlocks=blocks.Where(b=>b.marker!="[Historical interrupted work; reference only]").ToArray();
        // A final wire can be projected again after playback changes. Expand
        // the previous exact references before moving/projecting their targets.
        JToken Resolve(JToken value, int depth = 0) {
            if(depth>96)throw new JsonException("Private reference cycle.");
            if(value is JObject reference && reference.Count==1 && reference["privateFactRef"]?.Type==JTokenType.String) {
                var parts=((string)reference["privateFactRef"]).Split('/');
                if(parts.Length==2 && parts[0]=="pool" && int.TryParse(parts[1],out int poolIndex) && priorPool!=null && poolIndex>=0 && poolIndex<priorPool.Count)
                    return Resolve(priorPool[poolIndex],depth+1);
                if(parts.Length<2 || parts[0]!="document" || !int.TryParse(parts[1],out int index) || index<0 || index>=legacyBlocks.Length)
                    throw new JsonException("Unknown private reference.");
                JToken target=legacyBlocks[index].value;
                foreach(string part in parts.Skip(2))target=target is JArray array && int.TryParse(part,out int item) && item>=0 && item<array.Count
                    ? array[item] : (target as JObject)?[part.Replace("~1","/").Replace("~0","~")];
                if(target==null)throw new JsonException("Missing private reference target.");
                return Resolve(target,depth+1);
            }
            if(value is JObject inherited && inherited["privateFieldsRef"]?.Type==JTokenType.String) {
                var baseValue=Resolve(new JObject{["privateFactRef"]=inherited["privateFieldsRef"].DeepClone()},depth+1) as JObject;
                if(baseValue==null)throw new JsonException("Private fields reference must name an object.");
                foreach(var field in inherited.Properties().Where(p=>p.Name!="privateFieldsRef")) {
                    if(baseValue.Property(field.Name)!=null)throw new JsonException("Private fields collision.");
                    baseValue[field.Name]=Resolve(field.Value,depth+1);
                }
                return baseValue;
            }
            if(value is JObject obj)return new JObject(obj.Properties().Select(p=>new JProperty(p.Name,Resolve(p.Value,depth+1))));
            if(value is JArray rows)return new JArray(rows.Select(row=>Resolve(row,depth+1)));
            return value.DeepClone();
        }
        var expanded=blocks.Select(b=>(JObject)Resolve(b.value)).ToArray();
        for(int i=0;i<blocks.Count;i++)blocks[i].value=expanded[i];
        WorkReviewPlaybackProjection.Apply(blocks.Select(b=>b.value));
        var sharedValues=new JArray();int sharedCount=0;
        var fields = new JArray();
        var output = new StringBuilder(); int cursor = 0;
        foreach (var block in blocks)
        {
            if(interestOnly && block.marker=="[Autonomy runtime facts]")block.value=PrivateInterestContextPolicy.Project(block.value);
            if(interestOnly) {
                PrivateInterestScopePolicy.Project(block.value);
                if(block.marker=="[Task progress runtime]" && block.value["speech"] is JObject speech &&
                    (bool?)speech["currentOutputDrained"]==true && (bool?)speech["audioPlaying"]==false &&
                    speech["lastPublicPlayback"] is JObject playback && playback["completedPlaybackText"]?.Type==JTokenType.String) {
                    // Only output drafts/old replacement diagnostics are omitted;
                    // actual audio and all result/version-bound evidence remain.
                    if(speech["lastGeneratedText"]?.Type==JTokenType.String)speech.Remove("lastGeneratedText");
                    if(playback["publicText"]?.Type==JTokenType.String)playback.Remove("publicText");
                    if(playback["latestPlaybackRevision"] is JObject revision && revision.Properties().All(p=>new[]{"generation","userTurn","actionId","revision","observedUtc","before","after","publicText","currentRelativePose","arrivalObservedUtc","phaseNeutral","disposition","scope"}.Contains(p.Name)) &&
                        new[]{"before","after"}.All(k=>revision[k] is JObject o && o.Properties().All(p=>new[]{"actionId","phase","operationRevision","reportText","arrivalRelation","expectedReport"}.Contains(p.Name))) &&
                        revision["currentRelativePose"] is JObject pose && pose.Properties().All(p=>new[]{"currentlyNearAndFacingUser","userTrackingValid","userSupportVerified","actualUserDistance","actualFacingErrorDegrees","desiredUserDistance","observationFrame","observedAtRealtime"}.Contains(p.Name))) {
                        revision.Remove("before");revision.Remove("after");revision.Remove("currentRelativePose");
                    }
                }
            }
            WorkReviewInstructionProjection.ProjectRecord(block.value);
            foreach(var delivery in block.value.DescendantsAndSelf().OfType<JObject>().Where(o=>o["results"] is JArray).ToArray())
                foreach(var item in ((JArray)delivery["results"]).OfType<JObject>()) {
                    ResultPlaybackSelection.Annotate(item);
                    ResultPlaybackSelection.ProjectWireAudio(item);
                }
            // Runtime work packets also nest these under task/current-work.
            // Applying the same source-preserving index only at the root left
            // whole old result archives repeated in autonomous final requests.
            foreach (var property in block.value.Descendants().OfType<JProperty>().ToArray())
                if (new[] { "previousUnfinishedWork", "previousWorkContext" }.Contains(property.Name) && property.Value is JObject history)
                    property.Value = TaskContinuationContextPolicy.RoutingHistory(history, property.Path);
            if (interestOnly)
                foreach (var property in block.value.Descendants().OfType<JProperty>().ToArray())
                    if (new[] { "resultAtInterruption", "executionAtInterruption", "completedDeviceResult" }.Contains(property.Name) && property.Value is JObject outcome)
                        property.Value = TaskContinuationContextPolicy.IndexOutcome(outcome);
            if(block.value["previousUnfinishedWork"] is JObject previous)
                block.value["previousUnfinishedWork"]=TaskContinuationContextPolicy.RoutingHistory(previous);
            foreach(var property in block.value.Descendants().OfType<JProperty>().ToArray())
                if(new[]{"currentRoomTask","currentExecution"}.Contains(property.Name) && property.Value is JObject execution)
                    property.Value=ResultDeliveryContextPolicy.Execution(execution);
            if(block.marker=="[Historical interrupted work; reference only]" && block.value["snapshot"] is JObject snapshot)
                block.value["snapshot"]=TaskContinuationContextPolicy.RoutingHistory(snapshot,"previousUnfinishedWork");
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
                if ((property.Name == "sceneActionFacts" || property.Name == "sceneAction") && property.Value is JObject nested) Scene(nested, property.Path + ".");
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
        // Share after all omission passes so a reference cannot point at a
        // subtree that this same projection subsequently removes.
        var unshared=blocks.Select(b=>b.value.DeepClone()).ToArray();
        // Count across complete producer documents before replacing anything.
        // The pool stores exact typed values, including complete literal strings.
        // Partially overlapping receipts often differ only in observation time
        // or optional diagnostics. Share equal FIELDS, never merge differing
        // state/time. The complete typed expansion is verified below.
        var priorRecords=new System.Collections.Generic.List<JObject>();
        foreach(var node in blocks.SelectMany(b=>b.value.DescendantsAndSelf().OfType<JObject>()).ToArray()) {
            if(node.Parent==null || node.Count<4 || node["privateFieldsRef"]!=null || node["privateFactRef"]!=null)continue;
            var original=(JObject)node.DeepClone();JObject best=null;int saving=180;
            foreach(var prior in priorRecords.Where(p=>p.Parent!=null && p["privateFieldsRef"]==null)) {
                var common=new JObject(original.Properties().Where(p=>p.Name!="privateFieldsRef" && prior[p.Name]!=null &&
                    JToken.DeepEquals(prior[p.Name],p.Value)).Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
                int size=common.ToString(Formatting.None).Length;
                // A repeated object already has a cheaper whole-value path.
                if(common.Count>=3 && common.Count<original.Count && size>saving){best=common;saving=size;}
            }
            if(best!=null) {
                // Reuse an existing identical base; a new base pays its own
                // wire cost once, so first store a source path into a prior row.
                var prior=priorRecords.First(p=>p.Parent!=null && p["privateFieldsRef"]==null && best.Properties().All(f=>JToken.DeepEquals(p[f.Name],f.Value)));
                var existing=sharedValues.OfType<JObject>().FirstOrDefault(v=>JToken.DeepEquals(v,best));
                int index=existing==null?-1:sharedValues.IndexOf(existing);
                if(index>=0) {
                    foreach(var field in best.Properties())node.Remove(field.Name);
                    node["privateFieldsRef"]="pool/"+index;sharedCount++;
                } else {
                    // Materialize the common subset once and factor BOTH rows.
                    if(prior.Parent!=null && best.Properties().All(f=>prior.Property(f.Name)!=null)) {
                        index=sharedValues.Count;sharedValues.Add(best);
                        foreach(var field in best.Properties()){node.Remove(field.Name);prior.Remove(field.Name);}
                        node["privateFieldsRef"]="pool/"+index;prior["privateFieldsRef"]="pool/"+index;sharedCount+=2;
                    }
                }
            }
            priorRecords.Add(node);if(priorRecords.Count>128)priorRecords.RemoveAt(0);
        }
        var counts=new Dictionary<string,int>();
        string Key(JToken node)=>ActivityEvidencePolicy.Canonical(node).ToString(Formatting.None);
        bool Eligible(JToken node)=>node is JContainer?Key(node).Length>=120:
            node.Type==JTokenType.String&&((string)node).Length>=64;
        void Count(JToken node) {
            if(Eligible(node)){string k=Key(node);counts[k]=counts.TryGetValue(k,out int n)?n+1:1;}
            if(node is JContainer c)foreach(var child in c.Children())Count(child is JProperty p?p.Value:child);
        }
        foreach(var block in blocks)Count(block.value);
        var indices=new Dictionary<string,int>();
        JToken Share(JToken node,bool root=false) {
            string k=Eligible(node)?Key(node):null;
            if(!root&&k!=null&&counts[k]>1) {
                if(!indices.TryGetValue(k,out int index)) {
                    // Pool values may reference earlier pool entries only.
                    var value=Share(node,true);index=sharedValues.Count;sharedValues.Add(value);indices[k]=index;
                }
                sharedCount++;return new JObject{["privateFactRef"]="pool/"+indices[k]};
            }
            if(node is JObject o)return new JObject(o.Properties().Select(p=>new JProperty(p.Name,Share(p.Value))));
            if(node is JArray a)return new JArray(a.Select(v=>Share(v)));
            return node.DeepClone();
        }
        for(int i=0;i<blocks.Count;i++)blocks[i].value=(JObject)Share(blocks[i].value,true);
        JToken Expand(JToken node,int depth=0) {
            if(depth>96)throw new JsonException("Private pool cycle.");
            if(node is JObject r&&r.Count==1&&r["privateFactRef"]?.Type==JTokenType.String) {
                string key=(string)r["privateFactRef"];
                if(!key.StartsWith("pool/",StringComparison.Ordinal)||!int.TryParse(key.Substring(5),out int index)||index<0||index>=sharedValues.Count)throw new JsonException("Missing private pool value.");
                return Expand(sharedValues[index],depth+1);
            }
            if(node is JObject inherited && inherited["privateFieldsRef"]?.Type==JTokenType.String) {
                var restored=Expand(new JObject{["privateFactRef"]=inherited["privateFieldsRef"].DeepClone()},depth+1) as JObject;
                if(restored==null)throw new JsonException("Invalid private field base.");
                foreach(var p in inherited.Properties().Where(p=>p.Name!="privateFieldsRef")) {
                    if(restored.Property(p.Name)!=null)throw new JsonException("Private fields collision.");
                    restored[p.Name]=Expand(p.Value,depth+1);
                }
                return restored;
            }
            if(node is JObject o)return new JObject(o.Properties().Select(p=>new JProperty(p.Name,Expand(p.Value,depth+1))));
            if(node is JArray a)return new JArray(a.Select(v=>Expand(v,depth+1)));
            return node.DeepClone();
        }
        bool verified=blocks.Select((b,i)=>JToken.DeepEquals(ActivityEvidencePolicy.Canonical(unshared[i]),ActivityEvidencePolicy.Canonical(Expand(b.value)))).All(v=>v);
        int saved=unshared.Sum(v=>v.ToString(Formatting.None).Length)-blocks.Sum(b=>b.value.ToString(Formatting.None).Length)-sharedValues.ToString(Formatting.None).Length;
        if(!verified||saved<400){for(int i=0;i<blocks.Count;i++)blocks[i].value=(JObject)unshared[i];sharedValues.Clear();sharedCount=0;}
        output.Clear();cursor=0;fields.Clear();
        foreach(var block in blocks) {
            foreach(var field in block.original.Properties())fields.Add(new JObject{["block"]=block.marker,["field"]=field.Name,
                ["before"]=RoomDecisionValueMeasure(field.Value),["after"]=RoomDecisionValueMeasure(block.value[field.Name])});
            output.Append(context,cursor,block.start-cursor).Append(block.value.ToString(Formatting.None));cursor=block.end;
        }
        output.Append(context, cursor, context.Length - cursor);
        if(sharedCount>0)output.Append("\n[Private shared facts] ").Append(sharedValues.ToString(Formatting.None)).Append("\n[Private fact references] privateFactRef=pool/N is the exact typed value at index N in Private shared facts. Resolve all references including constraints and literal quotations. privateFieldsRef=pool/N copies exactly the fields in that object; inline fields are disjoint. Sharing grants no action.\n");
        var sharing=new SceneFactContextCompactor.Report();
        // Avoid composing two reference dialects which could move each other's targets.
        string result = sharedCount>0?output.ToString():SceneFactContextCompactor.Compact(output.ToString(), out sharing, true);
        diagnostic = new JObject { ["version"] = "work-progress-facts-v2", ["engineMemoryMutated"] = false,
            ["beforeCharacters"] = context.Length, ["afterCharacters"] = result.Length,
            ["beforeTextUnits"] = EstimateRoomDecisionTextUnits(context), ["afterTextUnits"] = EstimateRoomDecisionTextUnits(result),
            ["preservedOtherCharacters"] = context.Length - blocks.Sum(b => b.end - b.start),
            ["fields"] = fields, ["sharedSubtrees"] = sharedCount, ["privateReferencesRoundTripVerified"]=verified, ["exactSharing"] = JObject.FromObject(sharing),
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
