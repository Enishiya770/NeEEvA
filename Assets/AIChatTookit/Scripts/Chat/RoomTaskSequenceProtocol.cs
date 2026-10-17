using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Model-owned interpretation, engine-owned completion. No word-to-action rules.</summary>
public static class RoomTaskSequenceProtocol
{
    public const string Contract =
        "当taskSequenceRequired=true，先完整拆解本轮实际请求，再选本次可执行步骤，不能只处理最后一句。" +
        "taskSequence为空时输出taskPlan数组（最多6步），每步有id、quote、kind、desiredState、after。" +
        "同时输出taskCoverage：按原文顺序逐段覆盖完整currentUserText，每段quote逐字引用连续原文，段间只可略过空白和标点，不能略过任何实际文字。" +
        "每个独立要求都须单独检查，不能因后一句更长就漏掉开头的停止、取消或限制。每段kind=action/constraint/answer/context，stepIds列对应taskPlan步骤id；非action段stepIds为空。" +
        "先通读整句话判断改口和替换，再标注action：用户在同一句里改主意时，已撤回的早先动作只作为context逐字覆盖，不创建步骤、不选择执行，也不作为新步骤的前置。完整覆盖原文不等于把所有曾提到的动作累加执行。" +
        "例如先要求过来，随后改为先完成另一件事再过来，只有另一件事和随后过来两步；不能再插入一次提前过来。只是追加事项或再次强调时则保留仍然有效的先前要求，尤其不能漏掉没有被撤回的停止或约束。" +
        "语气词、称呼、重试前言和无关背景也必须用context段逐字覆盖，不能从第一个动作才开始引用。" +
        "每个taskPlan步骤须被action段覆盖。重复强调或同义改述同一次执行可由多段共同映射同一步，须独立语义核对；其quote原文须被这些段覆盖。不同对象、不同状态或明确再次执行必须分步，不得去重。背景、否定、条件和问题也要覆盖，但不得把它们伪造成执行要求。" +
        "currentFacts/execution表示仍在运行的旧动作，不等于已经停止。用户要求终止跟随或改做另一占用身体的任务时，先解释完整要求中的交接；确需停止的步骤用stop-moving，后续身体任务依赖其真实完成。" +
        "用户要求继续跟随、只回答或明确不要停止时，不得擅自创建停止步骤；停止当前移动也不等于撤销后半句新的设备操作。" +
        "停止若是为后续任务让出身体资源，该后续步骤的after必须包含停止步骤，即使用户以逗号连接；不能把必要交接写成独立步骤。" +
        "quote逐字摘自完整currentUserText的连续子句，保留否定和条件、不改变标点；历史只解释指代。" +
        "id用s1、s2等；kind为device-state、approach、follow、stop-moving、observation、other。" +
        "设备目标一整个寻找/靠近/开关/核验过程算一个device-state，desiredState为on/off，其余为null。" +
        "device-state只表达设置设备状态；撤回尚未执行的设备要求不是把设备设置成off，也不创建desiredState=null的device-state。实际取消通过可用的deviceGoal cancel提案表达，并保持本轮其它有效步骤。" +
        "单独到用户身边算approach；独立看、寻找、转向或靠近物件观察算observation；其它工具要求算other。" +
        "只列用户真实要求，问答/旧动作/已经搁置的话题不列步骤。没有动作要求输出空数组。" +
        "after列必须先完成的早先步骤id，明确的先后关系不能省略。独立步骤可以无依赖；不把接近设备当作已完成设备目标。" +
        "ORDERING: Before writing taskCoverage, resolve the FINAL valid actions and their execution order. Assign s1, s2, ... in that order, not the order of an abandoned mention. taskPlan must list prerequisites BEFORE their dependents; every after ID must already appear earlier in taskPlan. Only taskCoverage follows the original text order, including withdrawn clauses as context. Use those planned IDs in coverage. Never concatenate nonadjacent source spans into a quote." +
        "父任务的最终目的与当前步骤的立即操作必须分开。先观察/检索/准备，再修改/播放/返回：当前前置步骤只能带自身执行字段，后续操作保留在taskPlan，不得提前塞入当前决定。观察步骤的deviceGoal必须省略；只有选中device-state步骤才带set目标。询问是否看见是answer义务，不必凭空变成独立操作；若确需观察则单独规划并保留回答义务。" +
        "已有taskSequence时沿用其步骤，不输出新taskPlan；其中状态和回执由引擎提供，不能自行宣称完成或删除未完成项。" +
        "taskStep选择readyStepIds中的一个；执行必须符合该步骤kind与目标，device-state首次须附deviceGoal，其requestQuote须对应所选步骤的quote，不能选同类型的后续物件，不得用approach代替。" +
        "新taskPlan中无前置步骤的未完成项就是就绪步骤；决定执行时必须选择它的id。即使intent=none且附deviceGoal来启动统一设备任务，taskStep仍必须指向本次device-state，不能是null。纯粹说明无法形成可靠执行方案时，intent=none且无deviceGoal，taskStep=null；保留全部待办及前置，不把说明当成动作或完成。" +
        "完整原话、条件和对象仍约束本次决定，taskStep本身不授权动作。没有可执行步骤时taskStep=null、intent=none；" +
        "other只可交给正常工具通道，不能通过房间移动冒充完成。用户新取消/限制优先于旧任务。";

    private static readonly string[] Kinds = { "device-state", "approach", "follow", "stop-moving", "observation", "other" };
    private static JObject Parse(string text) => JObject.Parse(text ?? "", new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

    public static string DecisionOnly(string json)
    {
        try { var value = Parse(json); value.Remove("taskPlan"); value.Remove("taskCoverage"); value.Remove("taskStep"); return value.ToString(Formatting.None); }
        catch (Exception) { return json; }
    }

    public static string ExtendSchema(string schema, JObject input)
    {
        if ((bool?)input?["taskSequenceRequired"] != true) return schema;
        var sequence = input["taskSequence"] as JObject;
        bool operationRepair = (string)input.SelectToken("decisionRepair.rejectionReason") == "invalid-task-sequence-action-kind";
        var ready = sequence?["readyStepIds"] as JArray;
        var step = new JObject { ["type"] = new JArray("string", "null") };
        if (ready != null) step["enum"] = new JArray(ready.Select(x => x.DeepClone()).Concat(new[] { JValue.CreateNull() }));
        var plan = JObject.Parse("{\"type\":\"array\",\"maxItems\":6,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"id\",\"quote\",\"kind\",\"desiredState\",\"after\"],\"properties\":{\"id\":{\"type\":\"string\",\"maxLength\":16},\"quote\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":160},\"kind\":{\"type\":\"string\",\"enum\":[\"device-state\",\"approach\",\"follow\",\"stop-moving\",\"observation\",\"other\"]},\"desiredState\":{\"type\":[\"string\",\"null\"],\"enum\":[\"on\",\"off\",null]},\"after\":{\"type\":\"array\",\"maxItems\":5,\"uniqueItems\":true,\"items\":{\"type\":\"string\",\"maxLength\":16}}}}}");
        var result = Parse(schema);
        var coverage = JObject.Parse("{\"type\":\"array\",\"minItems\":1,\"maxItems\":24,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"quote\",\"kind\",\"stepIds\"],\"properties\":{\"quote\":{\"type\":\"string\",\"minLength\":1},\"kind\":{\"type\":\"string\",\"enum\":[\"action\",\"constraint\",\"answer\",\"context\"]},\"stepIds\":{\"type\":\"array\",\"maxItems\":6,\"uniqueItems\":true,\"items\":{\"type\":\"string\",\"maxLength\":16}}}}}");
        var actionCoverage = (JObject)coverage["items"].DeepClone();
        actionCoverage["properties"]["kind"]["enum"] = new JArray("action");
        actionCoverage["properties"]["stepIds"]["minItems"] = 1;
        var otherCoverage = (JObject)coverage["items"].DeepClone();
        otherCoverage["properties"]["kind"]["enum"] = new JArray("constraint", "answer", "context");
        otherCoverage["properties"]["stepIds"]["maxItems"] = 0;
        coverage["items"] = new JObject { ["oneOf"] = new JArray(actionCoverage, otherCoverage) };
        void Extend(JObject node)
        {
            var properties = node["properties"] as JObject;
            if (properties != null)
            {
                var ordered = new JObject();
                // Native decoding follows property order. Resolve the effective
                // plan before assigning source clauses to its steps, so an early
                // retracted clause cannot reserve the first executable step.
                // Full verbatim coverage remains mandatory and independently checked.
                if (sequence == null) { ordered["taskPlan"] = plan.DeepClone(); ordered["taskCoverage"] = coverage.DeepClone(); }
                ordered["taskStep"] = step.DeepClone();
                foreach (var p in properties.Properties()) ordered[p.Name] = p.Value.DeepClone();
                if (operationRepair && properties["deviceGoal"] == null &&
                    (properties["intent"]?["enum"] as JArray)?.Count == 1 && (string)properties["intent"]["enum"][0] == "none") {
                    // A repair can abstain, but cannot call that an executing
                    // observation/other step or retain an action-origin claim.
                    ordered["taskStep"] = new JObject { ["type"]="null" };
                    ordered["origin"]["enum"] = new JArray("none");
                    ordered["evidence"]["enum"] = new JArray("");
                }
                node["properties"] = ordered;
                if (!(node["required"] is JArray required)) node["required"] = required = new JArray();
                if (sequence == null) { required.Add("taskCoverage"); required.Add("taskPlan"); }
                required.Add("taskStep");
            }
            if (node["oneOf"] is JArray children) foreach (var child in children.OfType<JObject>()) Extend(child);
        }
        Extend(result); return result.ToString(Formatting.None);
    }

    public static bool TryPlan(string json, string currentRequest, out JArray plan, out string reason)
    {
        plan = null; reason = "invalid-task-sequence-plan";
        try
        {
            var rows = Parse(json)["taskPlan"] as JArray;
            if (rows == null || rows.Count > 6) return false;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!(row is JObject s) || s.Count != 5 || s["id"]?.Type != JTokenType.String ||
                    s["quote"]?.Type != JTokenType.String || s["kind"]?.Type != JTokenType.String || !(s["after"] is JArray after)) return false;
                string id = (string)s["id"], quote = (string)s["quote"], kind = (string)s["kind"];
                if (id.Length < 1 || id.Length > 16 || seen.Contains(id) || quote.Length < 1 || quote.Length > 160 ||
                    (currentRequest ?? "").IndexOf(quote, StringComparison.Ordinal) < 0 || !Kinds.Contains(kind) || after.Count > 5 ||
                    after.Any(x => x.Type != JTokenType.String || !seen.Contains((string)x)) || after.Values<string>().Distinct().Count() != after.Count) return false;
                if (kind == "device-state" ? s["desiredState"]?.Type != JTokenType.String || !new[] { "on", "off" }.Contains((string)s["desiredState"]) :
                    s["desiredState"]?.Type != JTokenType.Null) return false;
                seen.Add(id);
            }
            if (!TryCoverage(Parse(json)["taskCoverage"] as JArray, rows, currentRequest, out reason)) return false;
            plan = (JArray)rows.DeepClone(); reason = null; return true;
        }
        catch (Exception) { return false; }
    }

    public static bool TryCoverage(JArray coverage, JArray steps, string request, out string reason)
    {
        reason = "invalid-task-sequence-coverage";
        if (coverage == null || coverage.Count == 0 || coverage.Count > 24) return false;
        request = request ?? "";
        int cursor = 0;
        var mapped = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        var spans = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Tuple<int, int>>>();
        foreach (var item in coverage)
        {
            if (!(item is JObject row) || row.Count != 3 || row["quote"]?.Type != JTokenType.String ||
                row["kind"]?.Type != JTokenType.String || !(row["stepIds"] is JArray ids)) return false;
            string quote = (string)row["quote"], kind = (string)row["kind"];
            if (string.IsNullOrWhiteSpace(quote) || !(new[] { "action", "constraint", "answer", "context" }).Contains(kind) ||
                (kind == "action" ? ids.Count == 0 : ids.Count != 0)) return false;
            int start = request.IndexOf(quote, cursor, StringComparison.Ordinal);
            if (start < cursor || !request.Substring(cursor, start - cursor).All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c))) return false;
            cursor = start + quote.Length;
            if (ids.Values<string>().Distinct().Count() != ids.Count) return false;
            foreach (var id in ids)
            {
                if (id.Type != JTokenType.String) return false;
                var step = steps.OfType<JObject>().FirstOrDefault(s => (string)s["id"] == (string)id);
                if (step == null) return false;
                mapped.Add((string)id);
                if (!spans.TryGetValue((string)id, out var list)) spans[(string)id] = list = new System.Collections.Generic.List<Tuple<int, int>>();
                list.Add(Tuple.Create(start, cursor));
            }
        }
        if (mapped.Count != steps.Count || !request.Substring(cursor).All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c))) return false;
        foreach (var step in steps.OfType<JObject>())
        {
            string quote = (string)step["quote"];
            var list = spans[(string)step["id"]];
            int start = request.IndexOf(quote, StringComparison.Ordinal);
            bool covered = false;
            while (start >= 0)
            {
                covered = Enumerable.Range(start, quote.Length).All(i => char.IsWhiteSpace(request[i]) ||
                    char.IsPunctuation(request[i]) || list.Any(s => i >= s.Item1 && i < s.Item2));
                if (covered) break;
                start = request.IndexOf(quote, start + 1, StringComparison.Ordinal);
            }
            if (!covered) return false;
        }
        reason = null; return true;
    }

    // This is only a review candidate, never accepted without independent
    // semantics. Missing commands/conditions may NOT be silently called context.
    public static bool TryCoverageGapCandidate(string json, string request, out string candidate, out JArray gaps)
    {
        candidate = null; var proposedGaps = new JArray(); gaps = proposedGaps;
        try {
            var value = Parse(json);
            if (!(value["taskCoverage"] is JArray coverage) || !(value["taskPlan"] is JArray)) return false;
            var repaired = new JArray(); int cursor = 0;
            void Gap(int end) {
                string text = request.Substring(cursor, end - cursor);
                if (text.Any(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))) {
                    proposedGaps.Add(text);
                    repaired.Add(new JObject { ["quote"] = text, ["kind"] = "context", ["stepIds"] = new JArray() });
                }
            }
            foreach (var item in coverage.OfType<JObject>()) {
                string quote = (string)item["quote"];
                if (string.IsNullOrWhiteSpace(quote)) return false;
                int start = request.IndexOf(quote, cursor, StringComparison.Ordinal);
                if (start < cursor) return false;
                Gap(start); repaired.Add(item.DeepClone()); cursor = start + quote.Length;
            }
            Gap(request.Length);
            if (proposedGaps.Count == 0 || proposedGaps.Count > 4 || proposedGaps.Values<string>().Sum(s => s.Length) > 80) return false;
            value["taskCoverage"] = repaired;
            string proposed = value.ToString(Formatting.None);
            if (!TryPlan(proposed, request, out _, out _)) return false;
            candidate = proposed; return true;
        } catch (Exception) { return false; }
    }

    public static bool RequiresEquivalenceReview(string json)
    {
        try {
            var ids = ((JArray)Parse(json)["taskCoverage"]).OfType<JObject>()
                .SelectMany(row => ((JArray)row["stepIds"]).Values<string>());
            return ids.GroupBy(id => id).Any(group => group.Count() > 1);
        } catch (Exception) { return false; }
    }

    public static JArray LiteralQuoteFeedback(string json, string request)
    {
        var errors = new JArray(); request = request ?? "";
        void Add(string path, string reason, string quote) {
            if (errors.Count < 8) errors.Add(new JObject { ["path"] = path, ["reason"] = reason,
                ["quote"] = quote == null ? null : quote.Substring(0, Math.Min(160, quote.Length)) });
        }
        try {
            var input = Parse(json);
            var plan = input["taskPlan"] as JArray;
            if (plan != null) for (int i = 0; i < plan.Count; ++i) {
                string quote = plan[i]?["quote"]?.Type == JTokenType.String ? (string)plan[i]["quote"] : null;
                if (string.IsNullOrEmpty(quote) || request.IndexOf(quote, StringComparison.Ordinal) < 0)
                    Add("taskPlan[" + i + "].quote", "not-verbatim-current-user-text", quote);
            }
            var coverage = input["taskCoverage"] as JArray;
            if (coverage != null) {
                int cursor = 0;
                for (int i = 0; i < coverage.Count; ++i) {
                    string quote = coverage[i]?["quote"]?.Type == JTokenType.String ? (string)coverage[i]["quote"] : null;
                    int start = string.IsNullOrEmpty(quote) ? -1 : request.IndexOf(quote, cursor, StringComparison.Ordinal);
                    if (start < 0) { Add("taskCoverage[" + i + "].quote", "not-verbatim-in-remaining-source", quote); continue; }
                    string gap = request.Substring(cursor, start - cursor);
                    if (gap.Any(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)))
                        Add("taskCoverage[" + i + "]", "uncovered-original-text-before-this-row", gap);
                    cursor = start + quote.Length;
                }
                string rest = request.Substring(cursor);
                if (rest.Any(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))) Add("taskCoverage", "uncovered-original-text-at-end", rest);
            }
        } catch (Exception) { } // Diagnostic only; the strict parser remains the authority.
        return errors;
    }

    public static JArray StructureFeedback(string json)
    {
        var errors = new JArray();
        void Add(string path, string reason) { if (errors.Count < 8) errors.Add(new JObject { ["path"] = path, ["reason"] = reason }); }
        try
        {
            var input = Parse(json);
            if (!(input["taskPlan"] is JArray plan)) { Add("taskPlan", "required-array"); return errors; }
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Count; ++i)
            {
                if (!(plan[i] is JObject row)) { Add("taskPlan[" + i + "]", "required-step-object"); continue; }
                string id = row["id"]?.Type == JTokenType.String ? (string)row["id"] : null;
                if (string.IsNullOrEmpty(id) || seen.Contains(id)) Add("taskPlan[" + i + "].id", "required-unique-step-id");
                if (!(row["after"] is JArray after) || after.Any(x => x.Type != JTokenType.String || !seen.Contains((string)x)) || after.Distinct(JToken.EqualityComparer).Count() != after.Count)
                    Add("taskPlan[" + i + "].after", "prerequisites-must-be-unique-earlier-step-ids");
                if (id != null) seen.Add(id);
                string kind = (string)row["kind"];
                if (!Kinds.Contains(kind)) Add("taskPlan[" + i + "].kind", "unsupported-step-kind");
                if (kind == "device-state" ? row["desiredState"]?.Type != JTokenType.String || !new[] { "on", "off" }.Contains((string)row["desiredState"]) : row["desiredState"]?.Type != JTokenType.Null)
                    Add("taskPlan[" + i + "].desiredState", "device-state-requires-on-or-off-other-steps-require-null");
            }
            var mapped = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (!(input["taskCoverage"] is JArray coverage)) Add("taskCoverage", "required-array");
            else for (int i = 0; i < coverage.Count; ++i)
            {
                if (!(coverage[i] is JObject row) || !(row["stepIds"] is JArray ids)) { Add("taskCoverage[" + i + "].stepIds", "required-array"); continue; }
                bool action = (string)row["kind"] == "action";
                if (action && ids.Count == 0) Add("taskCoverage[" + i + "].stepIds", "action-must-reference-at-least-one-planned-step");
                if (!action && ids.Count != 0) Add("taskCoverage[" + i + "].stepIds", "non-action-must-not-reference-steps");
                if (ids.Distinct(JToken.EqualityComparer).Count() != ids.Count) Add("taskCoverage[" + i + "].stepIds", "step-ids-must-be-unique");
                foreach (var id in ids)
                    if (id.Type != JTokenType.String || !seen.Contains((string)id)) Add("taskCoverage[" + i + "].stepIds", "references-missing-planned-step");
                    else if (action) mapped.Add((string)id);
            }
            for (int i = 0; i < plan.Count; ++i)
                if (plan[i] is JObject row && row["id"]?.Type == JTokenType.String && !mapped.Contains((string)row["id"]))
                    Add("taskPlan[" + i + "]", "planned-step-needs-action-coverage");
            string selected = input["taskStep"]?.Type == JTokenType.String ? (string)input["taskStep"] : null;
            if (selected == null && ((string)input["intent"] != "none" || input["deviceGoal"] is JObject)) Add("taskStep", "select-a-ready-compatible-planned-step");
            else if (selected != null && !seen.Contains(selected)) Add("taskStep", "selection-references-missing-planned-step");
        }
        catch (Exception) { Add("$", "invalid-plan-structure"); }
        return errors; // Diagnostic only; never repairs or authorizes a decision.
    }

    public static JArray Ready(JArray steps) => new JArray(steps.OfType<JObject>().Where(s => (string)s["status"] == "pending" &&
        ((JArray)s["after"]).Values<string>().All(id => steps.OfType<JObject>().Any(p => (string)p["id"] == id && (string)p["status"] == "completed")))
        .Select(s => s["id"].DeepClone()));

    public static JObject SelectionFeedback(string json, JObject sequence)
    {
        try {
            var value = Parse(json);
            var steps = sequence?["steps"] as JArray ?? value["taskPlan"] as JArray;
            var selected = steps?.OfType<JObject>().FirstOrDefault(s => (string)s["id"] == (string)value["taskStep"]);
            return new JObject { ["selectedStep"] = selected?.DeepClone(),
                ["proposedIntent"] = value["intent"]?.DeepClone(), ["proposedOperation"] = value["deviceGoal"]?.DeepClone(),
                ["planCommitted"] = sequence != null,
                ["rule"] = "The current selection must match its step kind and prerequisites. observation cannot contain deviceGoal; later operations remain in the parent plan. device-state requires its own set goal. An uncommitted rejected plan may be rebuilt; committed steps/receipts cannot be rewritten. No operation was authorized by this feedback." };
        } catch (Exception) { return null; }
    }

    public static string RepairExecutionSchema(string schema, JObject input)
    {
        var repair=input?["decisionRepair"] as JObject;
        var feedback=repair?["sequenceSelectionFeedback"] as JObject;
        string kind=(string)feedback?.SelectToken("selectedStep.kind");
        if((int?)repair?["attempt"]!=1 || (int?)repair?["maximumAttempts"]!=1 ||
            (string)repair?["rejectionReason"]!="invalid-task-sequence-action-kind" ||
            !(feedback?["proposedOperation"] is JObject) || !Kinds.Contains(kind) || kind=="device-state")return schema;
        // One structural repair may defer an incompatible extra operation,
        // never turn it into an execution. Keep all ordinary action choices and
        // abstention, the complete parent request/plan, and normal validation.
        // Initial proposals and valid device-step proposals are unrestricted.
        // A different goal interpretation requires normal subsequent planning;
        // this retry cannot silently carry that later operation into this step.
        var result=Parse(schema);
        if(!(result["oneOf"] is JArray branches))return schema;
        foreach(var branch in branches.OfType<JObject>().ToArray())
            if(branch["properties"]?["deviceGoal"]!=null)branch.Remove();
        (result["properties"] as JObject)?.Remove("deviceGoal");
        return result.ToString(Formatting.None);
    }

    public static bool TrySelect(string json, RoomTaskProtocol.Decision decision, JArray steps, out string id, out string reason)
    {
        id = null; reason = "invalid-task-sequence-selection";
        try
        {
            var value = Parse(json); var token = value["taskStep"];
            if (token == null || token.Type != JTokenType.String && token.Type != JTokenType.Null) return false;
            id = (string)token;
            if (id == null) { bool noAction = decision.intent == "none" && decision.deviceGoal == null;
                if (noAction) reason = null; return noAction; }
            string selected = id;
            var step = steps.OfType<JObject>().FirstOrDefault(s => (string)s["id"] == selected);
            if (step == null || !Ready(steps).Values<string>().Contains(id)) { reason = "invalid-task-sequence-prerequisite"; return false; }
            string kind = (string)step["kind"];
            bool compatible = kind == "device-state" ? decision.deviceGoal?.operation == "set" && decision.deviceGoal.desiredState == (string)step["desiredState"] :
                kind == "other" ? decision.intent == "none" && decision.deviceGoal == null :
                kind == "observation" ? decision.deviceGoal == null && new[] { "inspect", "look-at", "approach-object", "inspect-object", "orient-view", "turn-in-place", "reposition-view", "search-scene" }.Contains(decision.intent) :
                decision.deviceGoal == null && decision.intent == kind;
            if (!compatible) { reason = "invalid-task-sequence-action-kind"; return false; }
            reason = null; return true;
        }
        catch (Exception) { return false; }
    }
}
