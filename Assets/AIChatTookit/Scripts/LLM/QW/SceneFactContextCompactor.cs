using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Request-only, exactly reversible sharing of repeated scene JSON and scope descriptions.
/// Current action/selection/state remain inline. No summaries, inference, truncation,
/// numeric rounding, history mutations or changes to the scene observation occur.</summary>
public static class SceneFactContextCompactor
{
    public const string ReferenceKey = "$sceneFactRef";
    private const int MinimumSubtreeCharacters = 384, MaximumReferences = 96;
    private const string Intro = "[Scene fact sharing v1] $sceneFactRef copies the EXACT earlier JSON value (object, array or scope-description string) at blockId#JSON-pointer (RFC6901). Read that earlier full value here too; this is NOT a new observation, action, permission or confirmation. Action/selection/device results appear in full at their first occurrence; an entirely identical later snapshot or objectContext may refer back. Different timestamps or state values are never merged.\n";
    private static readonly string[] Markers = {
        "[视觉关注与行动事实]",
        "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]",
        "[当前身体动作；程序事实]"
    };
    public sealed class Report
    {
        public int beforeCharacters, afterCharacters, blocks, references;
        public bool applied, roundTripVerified;
        public string reason = "no-sharing-benefit";
    }
    private sealed class Block
    {
        public int markerStart, start, end;
        public string id;
        public JObject original, compact;
    }
    public static string Compact(string context, out Report report)
    {
        report = new Report { beforeCharacters = context?.Length ?? 0, afterCharacters = context?.Length ?? 0 };
        if (string.IsNullOrEmpty(context) || context.Length > 2000000 || context.Contains("[Scene fact sharing v1]")) return context;
        try
        {
            var blocks = ReadBlocks(context);
            report.blocks = blocks.Count;
            if (blocks.Count == 0) return context;
            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            int references = 0, storedCharacters = 0;
            foreach (var block in blocks)
                block.compact = (JObject)Share(block.original, block.id + "#", false, known, ref references, ref storedCharacters);
            if (references == 0) return context;
            var lookup = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var block in blocks)
            {
                lookup.Add(block.id, block.compact);
                // Fail closed rather than send a lossy/mis-scoped reference encoding.
                if (!JToken.DeepEquals(block.original, Expand(block.compact, lookup, 0)))
                { report.reason = "round-trip-mismatch"; return context; }
            }
            report.roundTripVerified = true;
            var result = new StringBuilder(context.Length);
            int cursor = 0;
            foreach (var block in blocks)
            {
                result.Append(context, cursor, block.markerStart - cursor);
                if (cursor == 0) result.Append(Intro);
                result.Append("[Scene fact block ").Append(block.id).Append("]\n");
                result.Append(context, block.markerStart, block.start - block.markerStart);
                result.Append(block.compact.ToString(Formatting.None));
                cursor = block.end;
            }
            result.Append(context, cursor, context.Length - cursor);
            string value = result.ToString();
            if (value.Length + 128 >= context.Length) return context;
            report.references = references; report.applied = true; report.reason = "exact-scene-subtrees-and-descriptions";
            report.afterCharacters = value.Length;
            return value;
        }
        catch (JsonException) { report.reason = "invalid-scene-json-unchanged"; return context; }
        catch (ArgumentException) { report.reason = "invalid-scene-reference-unchanged"; return context; }
        catch (InvalidOperationException) { report.reason = "unsupported-scene-json-unchanged"; return context; }
        catch (OverflowException) { report.reason = "numeric-value-unchanged"; return context; }
    }

    private static List<Block> ReadBlocks(string context)
    {
        var result = new List<Block>();
        int userTail = context.IndexOf("(用户刚开口讲了下面这段话，请回应)", StringComparison.Ordinal);
        int bound = userTail >= 0 ? userTail : context.Length;
        for (int cursor = 0; cursor < bound && result.Count < 8;)
        {
            int next = context.IndexOf('\n', cursor);
            int lineEnd = next < 0 ? context.Length : next;
            foreach (string marker in Markers)
            {
                if (lineEnd - cursor < marker.Length || string.CompareOrdinal(context, cursor, marker, 0, marker.Length) != 0) continue;
                int start = cursor + marker.Length;
                while (start < bound && char.IsWhiteSpace(context[start])) start++;
                if (start >= bound || context[start] != '{') continue;
                int markerStart = cursor;
                int end = JsonObjectEnd(context, start, bound);
                if (end <= start) continue;
                if (marker == "[当前身体动作；程序事实]")
                {
                    // This existing builder appends the room runtime JSON AFTER the
                    // upper-body JSON. Leave that first document and its contract intact.
                    start = end;
                    while (start < bound && char.IsWhiteSpace(context[start])) start++;
                    if (start >= bound || context[start] != '{') continue;
                    end = JsonObjectEnd(context, start, bound);
                    if (end <= start) continue;
                    markerStart = start;
                }
                JObject value;
                string originalJson = context.Substring(start, end - start);
                using (var reader = new JsonTextReader(new StringReader(originalJson)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Decimal;
                    reader.MaxDepth = 96;
                    value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                }
                // Decimal parsing itself can round long mantissas or underflow tiny
                // values before the reference round-trip runs. Verify every original
                // numeric lexeme against its serialized value using exact digit/exponent
                // arithmetic, never another floating-point parse. Any uncertainty keeps
                // the entire request context byte-for-byte unchanged.
                VerifyNumericValues(originalJson, value.ToString(Formatting.None));
                JObject scene = value["sceneActionFacts"] as JObject ?? value;
                bool sceneFacts = scene["enabled"]?.Type == JTokenType.Boolean && scene["active"]?.Type == JTokenType.Boolean && scene["objectContext"] is JObject;
                bool roomFacts = marker == "[当前身体动作；程序事实]" && (string)value["source"] == "Unity-room-runtime" && value["currentFacts"] is JObject;
                if (!sceneFacts && !roomFacts) continue;
                if (value.SelectTokens("$..['" + ReferenceKey + "']").GetEnumerator().MoveNext()) continue;
                result.Add(new Block { markerStart = markerStart, start = start, end = end, id = "scene-facts-" + (result.Count + 1), original = value });
                lineEnd = end; next = context.IndexOf('\n', end);
                break;
            }
            cursor = next < 0 ? bound : next + 1;
        }
        return result;
    }

    private static void VerifyNumericValues(string original, string serialized)
    {
        using (var before = NumericValues(original).GetEnumerator())
        using (var after = NumericValues(serialized).GetEnumerator())
            while (true)
            {
                bool hasBefore = before.MoveNext(), hasAfter = after.MoveNext();
                if (hasBefore != hasAfter || hasBefore && before.Current != after.Current)
                    throw new OverflowException("scene-number-not-exactly-preserved");
                if (!hasBefore) return;
            }
    }

    private static IEnumerable<string> NumericValues(string json)
    {
        bool quoted = false, escaped = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c != '-' && (c < '0' || c > '9')) continue;
            int start = i;
            while (i + 1 < json.Length && "0123456789eE+-.".IndexOf(json[i + 1]) >= 0) i++;
            yield return CanonicalNumber(json.Substring(start, i - start + 1));
        }
    }

    private static string CanonicalNumber(string number)
    {
        int exponentIndex = number.IndexOfAny(new[] { 'e', 'E' });
        long exponent = 0;
        if (exponentIndex >= 0 && !long.TryParse(number.Substring(exponentIndex + 1),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            throw new OverflowException("scene-number-exponent-unsupported");
        string mantissa = exponentIndex < 0 ? number : number.Substring(0, exponentIndex);
        bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
        if (negative) mantissa = mantissa.Substring(1);
        int dot = mantissa.IndexOf('.');
        if (dot >= 0) exponent = checked(exponent - (mantissa.Length - dot - 1));
        string digits = mantissa.Replace(".", "").TrimStart('0');
        // Conservatively preserve signed zero too; normalization is only notation,
        // not permission to round, underflow or change a numeric value.
        if (digits.Length == 0) return negative ? "-0" : "0";
        int end = digits.Length;
        while (end > 1 && digits[end - 1] == '0') end--;
        exponent = checked(exponent + digits.Length - end);
        return (negative ? "-" : "") + digits.Substring(0, end) + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static int JsonObjectEnd(string text, int start, int bound)
    {
        int depth = 0; bool quoted = false, escaped = false;
        for (int i = start; i < bound; ++i)
        {
            char c = text[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') { if (--depth == 0) return i + 1; }
        }
        return -1;
    }

    private static JToken Share(JToken value, string pointer, bool historical,
        Dictionary<string, string> known, ref int references, ref int storedCharacters, string field = null)
    {
        // Only explicit program scope/capability descriptions may share scalar text.
        // Current reason, intent, entity, timestamps, speech and unknowns stay direct.
        bool description = value.Type == JTokenType.String && (field == "scope" || field == "capability" ||
            field != null && field.EndsWith("Scope", StringComparison.Ordinal));
        string exact = value is JContainer || description ? value.ToString(Formatting.None) : null;
        bool large = exact != null && exact.Length >= (description ? 180 : MinimumSubtreeCharacters);
        bool wholeRepeatedScene = value is JObject scene && scene["enabled"]?.Type == JTokenType.Boolean &&
            scene["active"]?.Type == JTokenType.Boolean && scene["objectContext"] is JObject && scene.Property("lastAction") != null;
        bool identicalObjectContext = field == "objectContext" && value is JObject;
        if (large && (historical || wholeRepeatedScene || identicalObjectContext || description) && references < MaximumReferences &&
            known.TryGetValue(exact, out string previous) && (!description || exact.Length >= previous.Length + ReferenceKey.Length + 106))
        { references++; return new JObject { [ReferenceKey] = previous }; }
        if (large && !known.ContainsKey(exact) && storedCharacters + exact.Length <= 8000000)
        { known.Add(exact, pointer); storedCharacters += exact.Length; }
        if (value is JObject obj)
        {
            var result = new JObject();
            foreach (var property in obj.Properties())
            {
                bool history = historical || ShareableDetail(property.Name);
                // These locations describe the CURRENT execution, even if a future schema nests them in history.
                if (property.Name == "selected" || property.Name == "lastAction" || property.Name == "stateResult" || property.Name == "currentAction") history = false;
                result[property.Name] = Share(property.Value, pointer + "/" + Escape(property.Name), history, known, ref references, ref storedCharacters, property.Name);
            }
            return result;
        }
        if (value is JArray array)
        {
            var result = new JArray();
            for (int i = 0; i < array.Count; i++) result.Add(Share(array[i], pointer + "/" + i, historical, known, ref references, ref storedCharacters));
            return result;
        }
        return value.DeepClone();
    }
    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");
    private static bool ShareableDetail(string name) => name == "recentObservations" || name == "knownObjectHistory" ||
        name == "localStructure" || name == "assessment" || name == "currentAssessment" ||
        name == "currentUserSupport" || name == "support" || name == "sceneTargetObservation" ||
        name == "approachObservation" || name == "streamTelemetry" || name == "lastObservedRejection" || name == "lastSceneTargetResult";

    private static JToken Expand(JToken value, Dictionary<string, JObject> blocks, int depth)
    {
        if (depth > 192) throw new InvalidOperationException("reference-depth");
        if (value is JObject obj)
        {
            if (obj.Count == 1 && obj[ReferenceKey]?.Type == JTokenType.String)
            {
                string target = (string)obj[ReferenceKey]; int hash = target.IndexOf('#');
                if (hash < 0 || !blocks.TryGetValue(target.Substring(0, hash), out JObject root)) throw new InvalidOperationException("reference-block");
                JToken found = root; string path = target.Substring(hash + 1);
                if (path.Length > 0)
                    foreach (string part in path.Substring(1).Split('/'))
                    {
                        string key = part.Replace("~1", "/").Replace("~0", "~");
                        found = found is JArray list ? list[int.Parse(key)] : found[key];
                        if (found == null) throw new InvalidOperationException("reference-path");
                    }
                return Expand(found, blocks, depth + 1);
            }
            var expanded = new JObject();
            foreach (var property in obj.Properties()) expanded[property.Name] = Expand(property.Value, blocks, depth + 1);
            return expanded;
        }
        if (value is JArray array)
        { var expanded = new JArray(); foreach (var item in array) expanded.Add(Expand(item, blocks, depth + 1)); return expanded; }
        return value.DeepClone();
    }
}
