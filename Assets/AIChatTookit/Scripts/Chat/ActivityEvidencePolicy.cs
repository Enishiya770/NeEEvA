using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Stable evidence identities and bounded attention; never execution authority.</summary>
public static class ActivityEvidencePolicy
{
    // Parse wire data without silently converting quoted timestamps into Date tokens.
    public static JObject Parse(string json)
    {
        using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            return JObject.Load(reader);
    }
    public static JToken Canonical(JToken value)
    {
        if (value == null) return JValue.CreateNull();
        if (value is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Canonical(p.Value))));
        if (value is JArray array) return new JArray(array.Select(Canonical));
        if (value is JValue scalar && scalar.Value == null) return JValue.CreateNull();
        // Date/string is a serializer representation difference, not a new event.
        if (value.Type == JTokenType.Date) return Canonical(new JValue(value.ToString(Formatting.None).Trim('"')));
        if (value.Type == JTokenType.String && ((string)value).Length >= 20 && ((string)value)[10] == 'T' &&
            DateTimeOffset.TryParseExact((string)value, new[] { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "o" },
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
            return new JValue(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        return value.DeepClone();
    }
    public static bool Equal(JToken a, JToken b) => JToken.DeepEquals(Canonical(a), Canonical(b));
    public static bool EqualLiveEvidence(JToken a,JToken b)
    {
        JToken Comparable(JToken source) {
            var copy=source?.DeepClone();
            if(copy is JContainer container)
                foreach(var row in container.DescendantsAndSelf().OfType<JObject>().ToArray())
                    if(row["userTrackingValid"]!=null && row["observationFrame"]!=null) {
                        row.Remove("observationFrame");row.Remove("observedAtRealtime");
                    }
            return copy;
        }
        return Equal(Comparable(a),Comparable(b));
    }
    public static string Stamp(JToken value)
    {
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(
            Encoding.UTF8.GetBytes(Canonical(value).ToString(Formatting.None)))).Replace("-", "").Substring(0, 16);
    }
    public static JArray Differences(JToken a, JToken b, string path = "")
    {
        var rows = new JArray();
        void Walk(JToken left, JToken right, string at)
        {
            if (Equal(left, right) || rows.Count >= 12) return;
            if (left is JObject x && right is JObject y) {
                foreach (string name in x.Properties().Select(p => p.Name).Union(y.Properties().Select(p => p.Name)))
                    Walk(x[name], y[name], at + "/" + name);
            } else rows.Add(new JObject { ["path"] = at, ["before"] = left?.DeepClone(), ["after"] = right?.DeepClone(),
                ["beforeType"] = left?.Type.ToString(), ["afterType"] = right?.Type.ToString() });
        }
        Walk(a, b, path); return rows;
    }

    public static JObject Receipt(JObject result)
    {
        var facts = result["facts"] as JObject;
        if (facts?["receipt"] != null) facts = WorkResultDeliveryProtocol.StepSummary(facts);
        JToken body = facts == null ? result["facts"]?.DeepClone() : facts.DeepClone();
        var binding=Binding(facts,(string)result["source"],(string)result["id"]);
        if(body is JObject obj)foreach(var field in obj.Descendants().OfType<JProperty>().ToArray()) {
            string bound=field.Name=="actionId"?"actionId":field.Name=="entityId"||field.Name=="boundEntityId"?"entity":
                field.Name=="desiredState"||field.Name=="operation"?"operation":field.Name=="actor"?"actor":null;
            if(bound!=null && Equal(field.Value,binding[bound]))field.Remove();
        }
        // Large opaque tool output remains in its ledger; no truncation may become counterevidence.
        bool complete = body != null && body.ToString(Formatting.None).Length <= 2200 &&
            (!(body is JObject record) || record.Count > 0);
        return new JObject { ["id"] = result["id"]?.DeepClone(), ["source"] = result["source"]?.DeepClone(),
            ["status"] = result["status"]?.DeepClone(), ["facts"] = complete ? body : null,
            ["detailsAvailable"] = complete, ["timeScope"] = "completed-event; not all current state",
            ["binding"] = binding };
    }
    public static JObject Binding(JObject facts, string operation, string id)
    {
        string Find(string key) => facts?.DescendantsAndSelf().OfType<JObject>()
            .Select(o => (string)(o[key] is JValue ? o[key] : null)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        return new JObject { ["actionId"] = Find("actionId") ?? id ?? "",
            ["operation"] = Find("desiredState") ?? Find("operation") ?? Find("intent") ?? Find("kind") ?? operation ?? "",
            ["entity"] = Find("entityId") ?? Find("boundEntityId") ?? Find("path") ?? Find("targetKind") ?? "unspecified",
            ["actor"] = Find("actor") ?? "character" };
    }
    public static void AddReceipts(JObject facts, JObject scopes, IEnumerable<JObject> results)
    {
        foreach (var result in results.Where(r => (string)r["source"] != "independent-answer").Reverse().Take(6).Reverse()) {
            var receipt = Receipt(result); string key = "result:" + (string)receipt["id"];
            facts[key] = receipt;
            if (scopes != null) scopes[key] = new JObject { ["domain"] = "execution",
                ["issues"] = (bool?)receipt["detailsAvailable"] == true ? new JArray("mistake", "uncertainty") : new JArray(),
                ["binding"] = receipt["binding"].DeepClone() };
        }
    }
    public static IEnumerable<JObject> AttentionFrames(JObject source)
    {
        foreach (var action in (source["actions"] as JArray ?? new JArray()).OfType<JObject>().Where(a=>(bool?)a["cancellable"]==true)) {
            var frame = (JObject)source.DeepClone(); frame["purpose"] = "action";
            frame["actions"] = new JArray(action.DeepClone()); frame["speech"] = new JArray(); frame.Remove("delivery");
            yield return Focus(frame);
        }
        foreach (var unit in (source["speech"] as JArray ?? new JArray()).OfType<JObject>()) {
            var frame = (JObject)source.DeepClone(); frame["purpose"] = "speech";
            frame["actions"] = new JArray(); frame["speech"] = new JArray(unit.DeepClone()); frame.Remove("delivery");
            foreach(var selected in SpeechOperations(frame))yield return Focus(selected);
        }
        if (source["delivery"] is JObject) {
            var frame = (JObject)source.DeepClone(); frame["purpose"] = "delivery";
            frame["actions"] = new JArray(); frame["speech"] = new JArray(); yield return Focus(frame);
        }
    }
    public static IEnumerable<JObject> SpeechOperations(JObject source)
    {
        if(source["reviewOperation"]!=null){yield return source;yield break;}
        var scopes=source["evidenceScopes"] as JObject??new JObject();var facts=source["facts"] as JObject??new JObject();
        var keys=scopes.Properties().Where(p=>(string)p.Value["domain"]=="execution" && p.Value["binding"] is JObject &&
            facts[p.Name] is JObject && (p.Value["issues"] as JArray)?.Count>0 &&
            (p.Name=="execution" || p.Name=="planningFailure" || p.Name.StartsWith("action:") || p.Name.StartsWith("result:")))
            // An inactive body retained from another task must not delay the
            // current adapter's result. It remains a separate later check.
            .OrderBy(p=>p.Name=="execution" && string.IsNullOrEmpty((string)facts[p.Name]["intent"]) &&
                new[]{"cancelled","idle","none"}.Contains((string)facts[p.Name].SelectToken("body.execution.phase"))?1:0)
            .GroupBy(p=>Stamp(p.Value["binding"])).Select(g=>g.First().Name).ToArray();
        // Multiple operations need independent attention. A true result for one
        // cannot mark a concurrent claim about the next operation as checked.
        if(keys.Length<2){yield return source;yield break;}
        string ClaimKey(string key) {
            string operation=(string)scopes[key]["binding"]["operation"]??"execution";
            return keys.Count(k=>(string)scopes[k]["binding"]["operation"]==operation)==1?operation:
                operation+" → "+(string)scopes[key]["binding"]["entity"]+" ["+(Array.IndexOf(keys,key)+1)+"]";
        }
        foreach(string key in keys) {
            var frame=(JObject)source.DeepClone();
            frame["reviewOperation"]=new JObject{["evidenceId"]=key,["binding"]=scopes[key]["binding"].DeepClone(),
                ["claimKey"]=ClaimKey(key),
                ["availableOperations"]=new JArray(keys.Select(k=>new JObject{
                    ["source"]=k,["claimKey"]=ClaimKey(k),["entity"]=scopes[k]["binding"]["entity"].DeepClone()}))};
            yield return frame;
        }
    }
    public static JObject Focus(JObject source)
    {
        var frame = (JObject)source.DeepClone(); var facts = (JObject)frame["facts"];
        var scopes = frame["evidenceScopes"] as JObject ?? new JObject(); frame["evidenceScopes"] = scopes;
        string purpose = (string)frame["purpose"];
        var actionKeys = new HashSet<string>((frame["actions"] as JArray ?? new JArray()).OfType<JObject>().Select(a => "action:" + (string)a["id"]));
        foreach (var field in facts.Properties().ToList()) {
            bool remove = purpose == "delivery" && field.Name != "constraints" ||
                field.Name.StartsWith("visual:") && (purpose!="speech" ||
                    !(frame["speech"] as JArray??new JArray()).OfType<JObject>().Any(u=>(string)u["id"]==(string)field.Value["speechId"])) ||
                purpose == "action" && new[] { "dialogue", "historyAnswers", "intention" }.Contains(field.Name) ||
                purpose == "action" && field.Name.StartsWith("recalled:",StringComparison.Ordinal) &&
                    !actionKeys.Any(key=>(facts[key] as JObject)?.Descendants().OfType<JProperty>().Any(p=>
                        new[]{"historyReferenceId","sourceCaptureId"}.Contains(p.Name)&&p.Value.Type==JTokenType.String&&
                        !string.IsNullOrEmpty((string)field.Value["sourceCaptureId"])&&
                        ((string)p.Value).Contains((string)field.Value["sourceCaptureId"]))==true) ||
                purpose == "action" && field.Name.StartsWith("action:") && !actionKeys.Contains(field.Name);
            if (remove) { facts.Remove(field.Name); scopes.Remove(field.Name); }
        }
        if (purpose == "speech") {
            // Only one phrase is judged; adjacent words supply referents, never a new target for correction.
            var dialogue = facts["dialogue"] as JArray;
            if (dialogue != null) facts["dialogue"] = new JArray(dialogue.OfType<JObject>().Where(d => (string)d["role"] == "assistant" || d["role"]==null && d["source"]!=null)
                .Reverse().Take(2).Reverse().Select(d => new JObject { ["text"] = d["text"], ["source"] = d["source"] }));
            facts.Remove("intention"); scopes.Remove("intention");
            // Adapter actions repeat the execution body's state. Speech needs
            // the task relationship, not a second, potentially sampled-later
            // copy of that same controller. Retain other executor facts.
            foreach(var field in facts.Properties().Where(p=>p.Name.StartsWith("action:")).ToArray())
                if(field.Value is JObject action && (string)action["adapter"]=="room" &&
                    (string)action["id"]==(string)facts["execution"]?["actionId"]) {
                    foreach(string name in new[]{"facts","phase","reason","cancellable","adapter","version"})action.Remove(name);
                    scopes.Remove(field.Name); // Only canonical execution can justify a correction.
                }
        }
        return frame;
    }
    public static string SemanticKey(JObject frame)
    {
        var selected = new JObject { ["owner"] = frame["owner"]?.DeepClone(), ["purpose"] = frame["purpose"]?.DeepClone(),
            ["reviewOperation"]=frame["reviewOperation"]?.DeepClone(),
            ["speechMeaning"]=frame["speechMeaning"]?.DeepClone(),["claimIndex"]=frame["claimIndex"]?.DeepClone(),
            ["delivery"] = frame["delivery"] == null ? null : new JObject { ["id"] = frame["delivery"]["id"],
                ["playbackRevision"] = frame["delivery"]["playbackRevision"] },
            ["speech"] = new JArray((frame["speech"] as JArray ?? new JArray()).OfType<JObject>().Select(u =>
                new JObject { ["id"] = u["id"], ["version"] = u["version"], ["text"] = u["text"] })),
            ["facts"] = frame["facts"]?.DeepClone() };
        var facts = selected["facts"] as JObject;
        facts?.Remove("dialogue"); facts?.Remove("intention");
        void Trim(JToken token) {
            if (token is JObject obj) {
                foreach (string name in new[] { "scope", "observedAt", "capturedAtRealtime", "elapsedSeconds" }) obj.Remove(name);
                if(obj["userTrackingValid"]!=null && obj["observationFrame"]!=null) {
                    obj.Remove("observationFrame");obj.Remove("observedAtRealtime");
                }
                // Continuous action checks retain executor distance guards. A
                // spoken current-state claim must be rechecked if its distance
                // or facing evidence changes after it was previously reviewed.
                if((string)frame["purpose"]=="action") {obj.Remove("actualUserDistance");obj.Remove("actualFacingErrorDegrees");}
                if(new[]{"moving","arrived","follow-moving","follow-waiting"}.Contains((string)obj["phase"]))obj.Remove("reason");
                foreach (var property in obj.Properties()) Trim(property.Value);
            } else if (token is JArray list) foreach (var item in list) Trim(item);
        }
        Trim(facts); return Stamp(selected);
    }

    // One canonical frozen body on the wire; the unit keeps its own result IDs
    // and image association. Resolve only a producer-owned matching speech ID.
    public static JObject FrozenSpeech(JObject unit,JObject frame,bool creationFallback=false)
    {
        var frozen=unit?["observedAtSpeechStart"] as JObject;
        if(frozen==null)return creationFallback?unit?["observedAtCreation"] as JObject:null;
        if((string)frozen["evidenceId"]!="speech-start")return frozen;
        var source=frame?["facts"]?["speech-start"] as JObject;
        if(source==null||(string)source["speechId"]!=(string)unit["id"])return null;
        var result=(JObject)source.DeepClone();result.Remove("speechId");result.Remove("sampleTime");
        foreach(var field in frozen.Properties())if(field.Name!="evidenceId")result[field.Name]=field.Value.DeepClone();
        return result;
    }

    public static JObject WithLiveFacts(JObject frame,JObject facts,JObject scopes)
    {
        var result=(JObject)frame.DeepClone();
        // Resolve wire aliases against THEIR frozen frame before replacing live
        // evidence. Otherwise the speech-start sample disappears at apply time.
        foreach(var unit in (result["speech"] as JArray??new JArray()).OfType<JObject>())
            if(FrozenSpeech(unit,result) is JObject frozen)unit["observedAtSpeechStart"]=frozen.DeepClone();
        result["facts"]=facts.DeepClone();result["evidenceScopes"]=scopes.DeepClone();
        return result;
    }

    public static JObject PlaybackProofSchema(JObject result)
    {
        JObject Fixed(string text) => new JObject { ["type"] = "string", ["enum"] = new JArray(text) };
        string completed = (string)result?["completedPlaybackText"] ?? "";
        JObject quotation = completed.Length == 0 ? Fixed("") : new JObject { ["type"] = "string", ["maxLength"] = 600 };
        int boundary = (int?)result?["latestCorrectionStart"] ?? -1;
        if (completed.Length > 0) {
            string tail = completed.Substring(boundary >= 0 && boundary < completed.Length ? boundary : 0);
            // Bind quotations to actual post-correction words, without deciding
            // whether those words are true, sufficient, or contradictory.
            var quotes = System.Text.RegularExpressions.Regex.Matches(tail, @"[^。！？.!?\r\n]+[。！？.!?]?")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Where(s => s.Length <= 600).ToList();
            if (tail.Length <= 600 && quotes.Count <= 1) quotes.Add(tail);
            if (quotes.Count > 0 && quotes.Count <= 32)
                quotation = new JObject { ["type"] = "string", ["enum"] = new JArray(quotes.Concat(new[]{""}).Distinct()) };
        }
        return new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JArray("revision", "lastRelevantQuote", "finalDisposition"), ["properties"] = new JObject {
                ["revision"] = result == null ? new JObject { ["type"] = "string", ["maxLength"] = 16 } : Fixed((string)result["playbackRevision"] ?? ""),
                ["lastRelevantQuote"] = quotation,
                ["finalDisposition"] = new JObject { ["type"] = "string", ["enum"] = completed.Length==0?
                    new JArray("unreported"):new JArray("consistent", "retracted", "unresolved", "unreported") } } };
    }
    public static string PlaybackRejection(JObject result, JObject proof, string quote, string completed)
    {
        if (result?["playbackRevision"] == null) return null; // Old persisted records have no ordered audit contract.
        string source = PlaybackSourceRejection(result, proof, completed);
        if(source != null) return source;
        if ((string)proof["finalDisposition"] != "consistent") return "playback-result-retracted-or-unresolved";
        if (string.IsNullOrWhiteSpace(quote) || (string)proof["lastRelevantQuote"] != quote || !(completed ?? "").Contains(quote))
            return "playback-latest-relevant-quote-required";
        if(result["latestCorrectionStart"]!=null && completed.LastIndexOf(quote,StringComparison.Ordinal)<(int)result["latestCorrectionStart"])
            return "playback-quote-precedes-result-correction";
        return null;
    }

    // Coverage, retraction and supplementation all cite the SAME played ledger.
    // Empty/unrelated playback permits an unreported result, never an invented quote.
    public static string PlaybackSourceRejection(JObject result, JObject proof, string completed)
    {
        if(result?["playbackRevision"] == null) return null;
        if(proof == null || (string)proof["revision"] != (string)result["playbackRevision"]) return "playback-revision-not-audited";
        string quote = (string)proof["lastRelevantQuote"], disposition = (string)proof["finalDisposition"];
        if(quote == null || !new[]{"consistent","retracted","unresolved","unreported"}.Contains(disposition)) return "playback-proof-incomplete";
        if(quote.Length == 0) return disposition == "unreported" ? null : "playback-disposition-without-spoken-evidence";
        if(!(completed ?? "").Contains(quote)) return "playback-quote-not-in-completed-audio";
        if(result["latestCorrectionStart"] != null && (completed ?? "").LastIndexOf(quote,StringComparison.Ordinal) < (int)result["latestCorrectionStart"])
            return "playback-quote-precedes-result-correction";
        return null;
    }
}
