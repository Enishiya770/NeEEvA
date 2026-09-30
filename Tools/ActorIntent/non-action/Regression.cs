using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class ActorNonActionContractRegression
{
    static int checks;
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }

    // This checker implements exactly the JSON Schema subset emitted by the
    // production factory. Unknown keywords fail the test rather than being ignored.
    static bool Matches(JToken value, JObject schema)
    {
        var supported = new HashSet<string> { "type", "enum", "anyOf", "additionalProperties", "required", "properties", "minLength", "maxLength", "minimum", "maximum" };
        foreach (var property in schema.Properties())
            if (!supported.Contains(property.Name)) throw new Exception("Unsupported schema assertion: " + property.Name);
        string type = (string)schema["type"];
        if (type == "object" && !(value is JObject) || type == "string" && value.Type != JTokenType.String ||
            type == "integer" && value.Type != JTokenType.Integer || type == "null" && value.Type != JTokenType.Null ||
            type == "number" && value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return false;
        if ((value.Type == JTokenType.Float || value.Type == JTokenType.Integer) &&
            (schema["minimum"] != null && (double)value < (double)schema["minimum"] || schema["maximum"] != null && (double)value > (double)schema["maximum"])) return false;
        if (schema["enum"] is JArray enums && !enums.Any(item => JToken.DeepEquals(item, value))) return false;
        if (schema["anyOf"] is JArray branches && !branches.Cast<JObject>().Any(branch => Matches(value, branch))) return false;
        if (value is JObject obj)
        {
            if (schema["required"] is JArray required && required.Values<string>().Any(key => obj[key] == null)) return false;
            if (schema["properties"] is JObject properties)
            {
                if ((bool?)schema["additionalProperties"] == false && obj.Properties().Any(p => properties[p.Name] == null)) return false;
                foreach (var property in obj.Properties())
                    if (properties[property.Name] is JObject child && !Matches(property.Value, child)) return false;
            }
        }
        if (value.Type == JTokenType.String)
        {
            int length = ((string)value).Length;
            if (schema["minLength"] != null && length < (int)schema["minLength"] ||
                schema["maxLength"] != null && length > (int)schema["maxLength"]) return false;
        }
        return true;
    }

    public static JObject Versioned(JObject value)
    {
        var copy = (JObject)value.DeepClone(); copy["version"] = ActorIntentProtocol.IntentVersion;
        if (copy["motionScope"] == null) copy["motionScope"] = (string)copy["domain"] == "head" ? "head_preset" : "none";
        if (copy["degrees"] == null) copy["degrees"] = JValue.CreateNull();
        if (copy["deviceState"] == null) copy["deviceState"] = "none";
        if (copy["targetEntityId"] == null) copy["targetEntityId"] = "";
        return copy;
    }

    public static int Run(List<JObject> fixedCases, List<JObject> existingCases)
    {
        checks = 0;
        var schema = (JObject)ActorIntentProtocol.ResponseFormat()["json_schema"]["schema"];
        Check(schema["anyOf"] is JArray families && families.Count == 3, "Semantic non-action/adoption/report families preserve model-selected conditions");
        foreach (var family in schema["anyOf"].Cast<JObject>())
        {
            Check((bool)family["additionalProperties"] == false && family["required"].Count() == 14,
                "Every grammar branch is self-contained and closed");
            var properties = ((JObject)family["properties"]).Properties().Select(p => p.Name).ToList();
            Check(properties.IndexOf("kind") < properties.IndexOf("goal"), "Semantic intent precedes goal generation");
        }
        Check(fixedCases.Count == 12, "Fixed five observed drafts and seven safety contrasts");
        int reproduced = 0;
        foreach (var item in fixedCases)
        {
            string id = (string)item["id"], draft = (string)item["draft"];
            var expected = Versioned((JObject)item["expected"]);
            Check(Matches(expected, schema), id + " intended classification remains expressible by production grammar");
            Check(ActorIntentProtocol.TryCompile(expected.ToString(Formatting.None), draft, out var canonical, out _) &&
                !canonical.RequiresFullReview && ActorIntentProtocol.Validate(canonical, draft, out _), id + " coherent supplied decision parses without structural repair");
            if (!(item["diagnosticEquivalent"] is JObject original)) continue;
            reproduced++;
            Check(!Matches(original, schema), id + " known inconsistent combination cannot be emitted by the new grammar");
            Check(!ActorIntentProtocol.TryParse(original.ToString(Formatting.None), draft, out _, out var reason) &&
                reason == (string)item["diagnosticReason"], id + " reproduces the exact logged rejection reason");
            Check(ActorIntentProtocol.TryCompile(original.ToString(Formatting.None), draft, out var conflict, out _) && conflict.RequiresFullReview,
                id + " unconstrained or old response still requires semantic review, not silent normalization");
            Check(!ActorIntentProtocol.Validate(conflict, draft, out _), id + " contradictory result cannot authorize publication/execution");
            var runtime = new FastLoopRuntime();
            Check(!runtime.TryApplyActor(conflict, new ActorDraft { Id = id, Revision = 1, Text = draft }, null, 0, out _, out _) && runtime.HeadTask == null,
                id + " conflicting wire cannot enter the head executor");
        }
        Check(reproduced == 5, "All five observed diagnostic conflicts reproduced");
        foreach (var item in existingCases.Where(c => (string)c["scope"] == "draft"))
            Check(Matches(Versioned((JObject)item["expected"]), schema), (string)item["id"] + " existing declared semantic outcome not removed by new grammar");

        var baseline = Versioned((JObject)fixedCases[0]["expected"]);
        var mutations = new Dictionary<string, string[]> {
            { "kind", new[] { "act", "wait", "claim", "cancel", "replan" } },
            { "domain", new[] { "head", "other_action" } },
            { "coverage", new[] { "atomic", "compound" } }, { "goal", new[] { "greeting", "我会抬头" } },
            { "task", new[] { "follow", "approach", "other" } }, { "target", new[] { "current_user", "other" } },
            { "action", new[] { "head.raise", "head.release" } }, { "condition", new[] { "user_count_three" } },
            { "evidence", new[] { "晚上好。" } }
        };
        foreach (var mutation in mutations)
            foreach (string value in mutation.Value)
            {
                var changed = (JObject)baseline.DeepClone(); changed[mutation.Key] = value;
                Check(!Matches(changed, schema), "none cannot hide " + mutation.Key + "=" + value);
            }
        foreach (var property in baseline.Properties().ToList())
        {
            var missing = (JObject)baseline.DeepClone(); missing.Remove(property.Name);
            Check(!Matches(missing, schema), "Every non-action field is required: " + property.Name);
            var nil = (JObject)baseline.DeepClone(); nil[property.Name] = JValue.CreateNull();
            Check(property.Name == "degrees" ? Matches(nil, schema) : !Matches(nil, schema), "Only degrees has an explicit nullable contract: " + property.Name);
        }
        var extra = (JObject)baseline.DeepClone(); extra["permission"] = true;
        Check(!Matches(extra, schema), "No extra execution field in either grammar family");

        var reported = Versioned((JObject)fixedCases.Single(c => (string)c["id"] == "contrast-completed")["expected"]);
        foreach (string task in new[] { "other", "approach", "follow" })
        {
            var mutated = (JObject)reported.DeepClone(); mutated["task"] = task;
            Check(!Matches(mutated, schema), "A completion claim cannot propose task=" + task);
        }
        foreach (string target in new[] { "current_user", "other" })
        {
            var mutated = (JObject)reported.DeepClone(); mutated["target"] = target;
            Check(!Matches(mutated, schema), "A completion claim cannot bind proposal target=" + target);
        }

        // A model can still misclassify language. We do not recognize words here
        // or claim that a canonical none result proves the draft has no action.
        // Contradictory committed goals must stay visible to the semantic reviewer.
        foreach (var item in fixedCases.Where(c => (string)c["expected"]["kind"] != "none"))
        {
            var conflict = Versioned((JObject)item["expected"]); conflict["kind"] = "none";
            string draft = (string)item["draft"];
            Check(!Matches(conflict, schema), (string)item["id"] + " commitment cannot retain goals in non-action branch");
            Check(ActorIntentProtocol.TryCompile(conflict.ToString(Formatting.None), draft, out var pending, out _) &&
                pending.RequiresFullReview && !ActorIntentProtocol.Validate(pending, draft, out _),
                (string)item["id"] + " inconsistent promise/condition/claim still reviewed");
        }
        Console.WriteLine("Actor non-action grammar: " + checks + " deterministic assertions, five diagnostic-equivalent log reproductions (not original raw JSON).");
        return checks;
    }
}
