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
        "quote逐字摘自完整currentUserText的连续子句，保留否定和条件、不改变标点；历史只解释指代。" +
        "id用s1、s2等；kind为device-state、approach、follow、stop-moving、observation、other。" +
        "设备目标一整个寻找/靠近/开关/核验过程算一个device-state，desiredState为on/off，其余为null。" +
        "单独到用户身边算approach；独立看、寻找、转向或靠近物件观察算observation；其它工具要求算other。" +
        "只列用户真实要求，问答/旧动作/已经搁置的话题不列步骤。没有动作要求输出空数组。" +
        "after列必须先完成的早先步骤id，明确的先后关系不能省略。独立步骤可以无依赖；不把接近设备当作已完成设备目标。" +
        "已有taskSequence时沿用其步骤，不输出新taskPlan；其中状态和回执由引擎提供，不能自行宣称完成或删除未完成项。" +
        "taskStep选择readyStepIds中的一个；执行必须符合该步骤kind与目标，device-state首次须附deviceGoal，其requestQuote须对应所选步骤的quote，不能选同类型的后续物件，不得用approach代替。" +
        "完整原话、条件和对象仍约束本次决定，taskStep本身不授权动作。没有可执行步骤时taskStep=null、intent=none；" +
        "other只可交给正常工具通道，不能通过房间移动冒充完成。用户新取消/限制优先于旧任务。";

    private static readonly string[] Kinds = { "device-state", "approach", "follow", "stop-moving", "observation", "other" };
    private static JObject Parse(string text) => JObject.Parse(text ?? "", new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

    public static string DecisionOnly(string json)
    {
        try { var value = Parse(json); value.Remove("taskPlan"); value.Remove("taskStep"); return value.ToString(Formatting.None); }
        catch (Exception) { return json; }
    }

    public static string ExtendSchema(string schema, JObject input)
    {
        if ((bool?)input?["taskSequenceRequired"] != true) return schema;
        var sequence = input["taskSequence"] as JObject;
        var ready = sequence?["readyStepIds"] as JArray;
        var step = new JObject { ["type"] = new JArray("string", "null") };
        if (ready != null) step["enum"] = new JArray(ready.Select(x => x.DeepClone()).Concat(new[] { JValue.CreateNull() }));
        var plan = JObject.Parse("{\"type\":\"array\",\"maxItems\":6,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"id\",\"quote\",\"kind\",\"desiredState\",\"after\"],\"properties\":{\"id\":{\"type\":\"string\",\"maxLength\":16},\"quote\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":160},\"kind\":{\"type\":\"string\",\"enum\":[\"device-state\",\"approach\",\"follow\",\"stop-moving\",\"observation\",\"other\"]},\"desiredState\":{\"type\":[\"string\",\"null\"],\"enum\":[\"on\",\"off\",null]},\"after\":{\"type\":\"array\",\"maxItems\":5,\"uniqueItems\":true,\"items\":{\"type\":\"string\",\"maxLength\":16}}}}}");
        var result = Parse(schema);
        void Extend(JObject node)
        {
            var properties = node["properties"] as JObject;
            if (properties != null)
            {
                var ordered = new JObject();
                if (sequence == null) ordered["taskPlan"] = plan.DeepClone();
                ordered["taskStep"] = step.DeepClone();
                foreach (var p in properties.Properties()) ordered[p.Name] = p.Value.DeepClone();
                node["properties"] = ordered;
                if (!(node["required"] is JArray required)) node["required"] = required = new JArray();
                if (sequence == null) required.Add("taskPlan");
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
            plan = (JArray)rows.DeepClone(); reason = null; return true;
        }
        catch (Exception) { return false; }
    }

    public static JArray Ready(JArray steps) => new JArray(steps.OfType<JObject>().Where(s => (string)s["status"] == "pending" &&
        ((JArray)s["after"]).Values<string>().All(id => steps.OfType<JObject>().Any(p => (string)p["id"] == id && (string)p["status"] == "completed")))
        .Select(s => s["id"].DeepClone()));

    public static bool TrySelect(string json, RoomTaskProtocol.Decision decision, JArray steps, out string id, out string reason)
    {
        id = null; reason = "invalid-task-sequence-selection";
        try
        {
            var value = Parse(json); var token = value["taskStep"];
            if (token == null || token.Type != JTokenType.String && token.Type != JTokenType.Null) return false;
            id = (string)token;
            if (id == null) return decision.intent == "none" && decision.deviceGoal == null && Ready(steps).Count == 0;
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
