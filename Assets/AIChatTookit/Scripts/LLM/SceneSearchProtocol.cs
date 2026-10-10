using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Search chooses request-listed evidence, never free image points or device commands.</summary>
public static class SceneSearchProtocol
{
    public sealed class Observation { public string referenceId, description; }
    public sealed class Decision
    {
        public string choice, referenceId, reason, selectionRole;
        public Observation[] observations = Array.Empty<Observation>();
    }

    public static readonly string Schema = BuildSchema();

    private static string BuildSchema() => new JObject {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JArray("choice", "referenceId", "selectionRole", "reason"),
        ["properties"] = new JObject {
            ["choice"] = new JObject { ["type"] = "string", ["enum"] = new JArray("current-candidate", "historical-object", "historical-view", "explore-region", "next-page", "done") },
            ["referenceId"] = new JObject { ["type"] = "string", ["maxLength"] = 256 },
            ["selectionRole"] = new JObject { ["type"] = "string", ["enum"] = new JArray("target", "landmark", "exploration") },
            ["reason"] = new JObject { ["type"] = "string", ["maxLength"] = 240 },
            ["observations"] = new JObject { ["type"] = "array", ["maxItems"] = 6,
                ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JArray("referenceId", "description"),
                    ["properties"] = new JObject {
                        ["referenceId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
                        ["description"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 } } } }
        }
    }.ToString(Newtonsoft.Json.Formatting.None);

    public const string Contract =
        "你负责角色一次有界环顾或寻物中的视觉判断，只返回JSON。task.originalUserRequest是保留的完整真实目标，不能用自己猜测的外观替换。" +
        "当前请求只含一张有明确imageRole和captureId的图片；历史与当前识别分开。按图中编号和本次候选表的referenceId选择，禁止编造坐标、编号、设备动作或找到结果。" +
        "requestChoices是本轮唯一可执行的choice/referenceId组合；只复制本轮短引用，sourceCaptureId/entityId等长标识仅为溯源，不是选择。" +
        "current-candidate只在find时表示本次当前图提供的候选可能是完整目标，稍后还有独立核验；survey只记录observations和选页/方向，不能挑一个物体当找到目标。historical-object表示回访记忆位置；historical-view表示先查看提供的历史图。" +
        "explore-region选择程序提供的未充分观察方向，Unity计算实际头/身体角度。next-page选择availablePages中尚未检查的页；本页没找到不代表整个画面没有。" +
        "imageRole=historical时，这张图只用于历史召回；根据该图的实际目标区域提出线索，不能把历史目标叫作当前可见；按请求提供的历史引用返回，回访后必须拍新图。" +
        "observations可用简短词组描述当前候选表最多6个实际看见的物件，必须逐字引用其referenceId；这些描述是视觉判断，不是用户授权或目标确认。不要把旧图或对话里的物体名称贴在当前空表面。" +
        "find目标明确可见则先选候选，不能为看一圈而错过当前目标。完整目标的空间关系、用途和限定必须同时满足；目标所在表面、附近背景或同大类相似物不能替代目标。不能因候选最显眼或离目标最近就当目标。" +
        "必须分别判断目标本体与参照物。selectionRole=target只表示候选可能是请求要找的物件本体；landmark表示它只是描述中用于定位的家具、容器或背景；exploration表示查看未确认区域/历史图/页或停止。current-candidate只能选择target；不能把参照物送作目标核验。historical-object可选择target或landmark，但先遍历全部有效memory寻找目标本体线索，再考虑参照物。已有可信目标本体线索时，不应只因为参照物更近、更显眼、在列表前面而先选参照物。" +
        "targetRetrieval列出本次全部可选物件的语义线索，必须结合完整请求比较目标本体与参照关系。Unity名称和空间相邻关系仍只是检索线索，不是目标确认。回访历史target不会绑定实体或授权设备写入；当前新图仍须独立匹配。recovery.onlyNewView=true时只选未回访记忆或有新增覆盖价值的方向，不能重复失败候选或重新声明已找到。" +
        "当前图无合适候选时优先使用确有依据的历史线索或有新增观察价值的区域；不要反复检查同一点。reviewed表示实际已查看此页/物件，selectionAllowed=false不允许选。角度以提供的相对当前camera/body值为准，负yaw向左、正yaw向右；不要把region编号或起始角度当当前角度。" +
        "先检索已提供的历史图与冻结位置线索，再探索未知方向。目录的authoredHints/sceneLabel是这些已观察像素对应的Unity来源名称，可帮助检索，但不是独立视觉确认；modelDescription只是模型旧判断。匹配完整目标与其空间关系，不能把‘在某家具上’改成‘在任意台面上’，不能把参照家具本身、旁边物体或同类物件替代目标。" +
        "不以房间常见布局、物件通常放在哪边或自身猜想指定目标方位。没有相关历史线索才选择尚未充分观察区域，并明确这是未知区域探索。历史点是冻结世界位置；身体和相机相对角度已由Unity重算，不能沿用移动前的左右、把头转当身体转或把相机方向当世界轴。" +
        "historicalCandidates只对应当前这张历史编号图：可选其中historical-object作回访方向；observations也只能描述该历史图提供的候选。它们都不证明当前可见或目标找到，必须回访后重新拍摄并独立核验。" +
        "survey要获取不同方向的实际观察与物件记录，是否完成由实测覆盖决定；find的done仅表示停止/需要澄清，不表示找到或目标不存在。" +
        "choice=done时referenceId必须为空；其它choice必须逐字引用当前输入提供的对应referenceId。reason简短说明此步依据，不包含假想执行结果。";

    public sealed class ReferenceScope
    {
        internal readonly Dictionary<string, string> choices = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> observations = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // The model only copies short request-local names. Engine capture/entity keys
    // stay in this map and are still checked by the actual execution path.
    public static JObject CreateScopedInput(JObject source, string requestId, out ReferenceScope scope)
    {
        scope = new ReferenceScope(); var result = (JObject)source.DeepClone();
        var map = scope; var choices = new JArray();
        string Alias(JObject item, string prefix, int index, string choice, bool allow, bool note = false)
        {
            string original = (string)(item["referenceId"] ?? item["selectionId"]);
            if (string.IsNullOrEmpty(original)) return null;
            string alias = requestId + "-" + prefix + index;
            item["referenceId"] = alias;
            if (item.Property("selectionId") != null) item["selectionId"] = alias;
            if (note) map.observations.Add(alias, original);
            if (allow) { map.choices.Add(choice + ":" + alias, original); choices.Add(new JObject { ["choice"] = choice, ["referenceId"] = alias }); }
            return alias;
        }
        void Rows(JToken rows, string prefix, string choice, bool allowed, bool notes = false)
        {
            if (!(rows is JArray array)) return;
            int index = 0;
            foreach (var item in array.OfType<JObject>())
                Alias(item, prefix, ++index, choice, allowed && (bool?)item["selectionAllowed"] != false, notes);
        }
        if ((string)result["imageRole"] == "historical")
        {
            string original = (string)result["historicalReferenceId"];
            var item = new JObject { ["referenceId"] = original };
            result["historicalReferenceId"] = Alias(item, "h", 1, "historical-view", true);
            Rows(result["historicalCandidates"], "m", "historical-object", true, true);
        }
        else
        {
            var catalog = result["visibleCatalog"] as JObject;
            var current = catalog?["current"] as JObject;
            bool find = (string)result["task"]?["mode"] == "find";
            Rows(current?["candidates"], "c", "current-candidate", find, true);
            Rows(current?["availablePages"], "p", "next-page", true);
            Rows(catalog?["memory"], "m", "historical-object", find);
            Rows(catalog?["historicalViews"], "h", "historical-view", find);
            Rows(result["explorationRegions"], "r", "explore-region", true);
        }
        choices.Add(new JObject { ["choice"] = "done", ["referenceId"] = "" });
        result["requestId"] = requestId; result["requestChoices"] = choices;
        var retrieval = new JArray();
        foreach (var rows in new[] { result["visibleCatalog"]?["current"]?["candidates"], result["visibleCatalog"]?["memory"], result["historicalCandidates"] })
            if (rows is JArray entries)
                foreach (var entry in entries.OfType<JObject>())
                    if ((bool?)entry["selectionAllowed"] != false)
                        retrieval.Add(new JObject {
                            ["referenceId"] = entry["referenceId"]?.DeepClone(),
                            ["entityId"] = entry["entityId"]?.DeepClone(),
                            ["historical"] = (bool?)entry["historical"] == true || (string)result["imageRole"] == "historical",
                            ["authoredNames"] = new JObject { ["renderer"] = entry["authoredHint"]?["rendererName"]?.DeepClone(),
                                ["entityRoot"] = entry["authoredHint"]?["entityRootName"]?.DeepClone(),
                                ["sceneLabel"] = entry["authoredHint"]?["sceneLabel"]?.DeepClone() },
                            ["modelDescription"] = entry["modelDescription"]?.DeepClone(),
                            ["measuredNeighborEntityId"] = entry["geometryNeighbor"]?["entityId"]?.DeepClone(),
                            ["measuredNeighborDistanceMeters"] = entry["geometryNeighbor"]?["observedPointDistanceMeters"]?.DeepClone(),
                            ["semanticStatus"] = entry["semanticStatus"]?.DeepClone(),
                            ["scope"] = "Compare the requested object itself with its landmark; this is a retrieval clue, never identity confirmation or action authority." });
        result["targetRetrieval"] = retrieval;
        // Old receipts remain useful evidence but are never a source of action IDs.
        foreach (var history in new[] { result["lastStep"], result["task"]?["receipts"] })
            if (history is JContainer container)
                foreach (var property in container.Descendants().OfType<JProperty>().Where(p => p.Name == "referenceId").ToArray()) property.Remove();
        return result;
    }

    public static bool TryResolveDecision(string json, ReferenceScope scope, out Decision decision, out string reason)
    {
        if (!TryDecision(json, out decision, out reason)) return false;
        if (scope == null) { reason = "search-reference-scope-unavailable"; decision = null; return false; }
        if (decision.choice != "done")
        {
            if (!scope.choices.TryGetValue(decision.choice + ":" + decision.referenceId, out string original))
            { reason = "search-choice-not-offered-in-current-request"; decision = null; return false; }
            decision.referenceId = original;
        }
        foreach (var observation in decision.observations)
        {
            if (!scope.observations.TryGetValue(observation.referenceId, out string original))
            { reason = "search-description-not-from-current-request"; decision = null; return false; }
            observation.referenceId = original;
        }
        return true;
    }

    public static string SchemaForInput(string input)
    {
        var schema = JObject.Parse(Schema);
        var source = JObject.Parse(input ?? "{}");
        if (!(source["requestChoices"] is JArray offered) || offered.Count == 0) return schema.ToString(Newtonsoft.Json.Formatting.None);
        var choices = offered.OfType<JObject>().ToArray();
        schema["properties"]["choice"]["enum"] = new JArray(choices.Select(row => (string)row["choice"]).Distinct());
        schema["properties"]["referenceId"]["enum"] = new JArray(choices.Select(row => (string)row["referenceId"]).Distinct());
        var notes = (((string)source["imageRole"] == "historical" ? source["historicalCandidates"] :
            source["visibleCatalog"]?["current"]?["candidates"]) as JArray)?.OfType<JObject>()
            .Select(row => (string)row["referenceId"]).Where(value => !string.IsNullOrEmpty(value)).ToArray() ?? Array.Empty<string>();
        if (notes.Length == 0) schema["properties"]["observations"]["maxItems"] = 0;
        else schema["properties"]["observations"]["items"]["properties"]["referenceId"]["enum"] = new JArray(notes);
        // The local decoder needs complete standalone object branches. Mixing a
        // root object with partial oneOf property overrides can drop required keys.
        var branches = new JArray();
        foreach (var group in choices.GroupBy(row => (string)row["choice"]))
        {
            var branch = (JObject)schema.DeepClone();
            branch["properties"]["choice"]["enum"] = new JArray(group.Key);
            branch["properties"]["referenceId"]["enum"] = new JArray(group.Select(row => (string)row["referenceId"]));
            branch["properties"]["selectionRole"]["enum"] = group.Key == "current-candidate" ? new JArray("target") :
                group.Key == "historical-object" ? new JArray("target", "landmark") : new JArray("exploration");
            branches.Add(branch);
        }
        return new JObject { ["oneOf"] = branches }.ToString(Newtonsoft.Json.Formatting.None);
    }

    // Read-only replay validation against an already scoped production input.
    // This validates the exact choice set, but cannot grant runtime ownership,
    // fresh-pixel validity or access to the private original-reference map.
    public static bool TryDecisionForInput(string json, string input, out Decision decision, out string reason)
    {
        decision = null; reason = "search-request-choice-set-unavailable";
        try
        {
            var source = JObject.Parse(input ?? "{}");
            if (!(source["requestChoices"] is JArray offered) || offered.Count == 0) return false;
            var scope = new ReferenceScope();
            foreach (var row in offered.OfType<JObject>())
            {
                string choice = (string)row["choice"], reference = (string)row["referenceId"];
                if (choice != "done" && !string.IsNullOrEmpty(choice) && !string.IsNullOrEmpty(reference))
                    scope.choices.Add(choice + ":" + reference, reference);
            }
            var notes = (string)source["imageRole"] == "historical" ? source["historicalCandidates"] : source["visibleCatalog"]?["current"]?["candidates"];
            if (notes is JArray candidates)
                foreach (var candidate in candidates.OfType<JObject>())
                {
                    string reference = (string)candidate["referenceId"];
                    if (!string.IsNullOrEmpty(reference)) scope.observations.Add(reference, reference);
                }
            return TryResolveDecision(json, scope, out decision, out reason);
        }
        catch (Exception) { return false; }
    }

    public static bool TryDecision(string json, out Decision decision, out string reason)
    {
        decision = null; reason = "invalid-scene-search-decision";
        try
        {
            var value = JObject.Parse(json ?? "", new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (value.Count < 4 || value.Count > 5 || value.Properties().Any(p => !new[] { "choice", "referenceId", "selectionRole", "reason", "observations" }.Contains(p.Name)) ||
                new[] { "choice", "referenceId", "selectionRole", "reason" }.Any(k => value[k]?.Type != JTokenType.String)) return false;
            string choice = (string)value["choice"], id = (string)value["referenceId"], detail = (string)value["reason"];
            string role = (string)value["selectionRole"];
            if (!new[] { "current-candidate", "historical-object", "historical-view", "explore-region", "next-page", "done" }.Contains(choice) ||
                id.Length > 256 || detail.Length > 240 || (choice == "done" ? id.Length != 0 : string.IsNullOrWhiteSpace(id))) return false;
            if (choice == "current-candidate" ? role != "target" : choice == "historical-object" ?
                role != "target" && role != "landmark" : role != "exploration")
            { reason = "search-target-landmark-role-conflict"; return false; }
            var observations = new System.Collections.Generic.List<Observation>();
            if (value.Property("observations") != null)
            {
                if (!(value["observations"] is JArray rows) || rows.Count > 6) return false;
                foreach (var token in rows)
                {
                    if (!(token is JObject row) || row.Count != 2 || row["referenceId"]?.Type != JTokenType.String || row["description"]?.Type != JTokenType.String) return false;
                    string reference = (string)row["referenceId"], description = (string)row["description"];
                    if (string.IsNullOrWhiteSpace(reference) || reference.Length > 256 || string.IsNullOrWhiteSpace(description) || description.Length > 120 ||
                        observations.Any(x => x.referenceId == reference)) return false;
                    observations.Add(new Observation { referenceId = reference, description = description });
                }
            }
            decision = new Decision { choice = choice, referenceId = id, reason = detail, selectionRole = role, observations = observations.ToArray() };
            reason = null; return true;
        }
        catch (Exception) { return false; }
    }
}
