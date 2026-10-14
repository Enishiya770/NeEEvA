using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// The independent room planner also checks the proposed intention's factual
// premise. This adds no model round and grants no action authority.
public static class AutonomyPremiseProtocol
{
    public const string Rejected = "autonomy-premise-rejected";
    public const string Contract =
        "先核对意图的事实前提，再选动作。无动作不等于没有事实前提：即使只打算聊天，意图里关于用户说过、做过、要求过或当前状态的断言也要核实。" +
        "claim逐字复制含有事件/状态前提的autonomousIntention；只提议一个新话题/兴趣且无事件断言时才留空。先找证据，再写reason作比较，最后给verdict。" +
        "找不到支持事件的原话/观察就用unsupported；不能把提案本身、没有新动作、与人设相容当成证据。" +
        "区分请求与执行：‘用户让我做X’只声称存在请求，不声称X已完成；已记录的请求原话就支持请求发生，执行完成另看回执。" +
        "自主tick中的lastHistoricalUserText已经是收到过的历史发言，不能因为它使用命令语气就说它尚未发生，也不要求用户在本tick再次下令。" +
        "Before choosing an autonomous action, independently check EVERY factual presupposition of autonomousIntention. " +
        "That private proposal is NOT evidence that the user said/felt/did something. Persona, wishes, imagined stories and assistant guesses do not establish user events. " +
        "autonomyPremise.verdict is no-factual-premise for a freely chosen prospective interest/question with no claimed past/current event; supported only when all premises have supplied evidence; otherwise unsupported or uncertain. " +
        "Use source=dialogue and a short EXACT quote from recentDialogue for dialogue premises; the engine locates that quote. Check role, negation, hypothetical wording and event time. " +
        "recentDialogue is chronological, oldest first. source=last-user-utterance may quote input.currentUserText (the latest HISTORICAL user utterance on an autonomous tick); it is not a new command. " +
        "A user's confession, request, promise, consent or emotion cannot be inferred from body proximity, successful tools or an image. " +
        "source=current-state may quote currentFacts/execution/lastActionResults for actual engine facts. source=current-image with empty quote may support only what the attached CURRENT image visibly establishes. " +
        "For no-factual-premise/unsupported/uncertain use empty source and quote. Unsupported/uncertain requires intent=none, origin=none, evidence=''. " +
        "Do not invent a premise to justify a new topic, rewrite the proposal to hide its premise, or replay an old task. A supported intention still needs the usual independent action validation. " +
        "Return autonomyPremise alongside the normal decision fields; keep its reason concise.";

    public static bool Required(string input)
    { try { return (bool?)JObject.Parse(input)["autonomous"] == true; } catch (Exception) { return false; } }

    public static string ExtendSchema(string schema, string input)
    {
        var root = JObject.Parse(schema);
        void Add(JObject branch)
        {
            if (branch["properties"] is JObject properties)
            {
                properties["autonomyPremise"] = JObject.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"verdict\",\"source\",\"quote\",\"reason\"],\"properties\":{\"verdict\":{\"type\":\"string\",\"enum\":[\"no-factual-premise\",\"supported\",\"unsupported\",\"uncertain\"]},\"source\":{\"type\":\"string\",\"maxLength\":40},\"quote\":{\"type\":\"string\",\"maxLength\":200},\"reason\":{\"type\":\"string\",\"maxLength\":160}}}");
                var premise=(JObject)properties["autonomyPremise"];
                var prior=(JObject)premise["properties"];
                premise["properties"]=new JObject { ["reason"]=prior["reason"].DeepClone(),["verdict"]=prior["verdict"].DeepClone(),
                    ["claim"]=new JObject { ["type"]="string",["maxLength"]=200 },
                    ["source"]=prior["source"].DeepClone(),["quote"]=prior["quote"].DeepClone(),
                };
                premise["required"]=new JArray("reason","verdict","claim","source","quote");
                string intention=(string)JObject.Parse(input)["autonomousIntention"]??"";
                var noFact=(JObject)premise.DeepClone();
                foreach(string field in new[]{"claim","source","quote"})noFact["properties"][field]["enum"]=new JArray("");
                noFact["properties"]["verdict"]["enum"]=new JArray("no-factual-premise");
                var factual=(JObject)premise.DeepClone();
                factual["properties"]["claim"]=new JObject { ["type"]="string",["enum"]=new JArray(intention) };
                factual["properties"]["source"]["enum"]=new JArray("","dialogue","last-user-utterance","current-state","current-image");
                factual["properties"]["verdict"]["enum"]=new JArray("supported","unsupported","uncertain");
                properties["autonomyPremise"]=new JObject { ["oneOf"]=new JArray(noFact,factual) };
                ((JArray)branch["required"]).Add("autonomyPremise");
                var ordered = new JObject { ["autonomyPremise"] = properties["autonomyPremise"].DeepClone() };
                foreach (var field in properties.Properties()) if (field.Name != "autonomyPremise") ordered[field.Name] = field.Value.DeepClone();
                branch["properties"] = ordered;
            }
            if (branch["oneOf"] is JArray branches) foreach (JObject child in branches) Add(child);
        }
        Add(root); return root.ToString(Formatting.None);
    }

    public static bool TryResolve(string input, bool image, string output, out string decision, out JObject premise)
    {
        decision = null; premise = null;
        try
        {
            var value = JObject.Parse(output, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            premise = value["autonomyPremise"] as JObject;
            if (premise == null || premise.Count != 5) return false;
            foreach (string key in new[] { "claim", "verdict", "source", "quote", "reason" }) if (premise[key]?.Type != JTokenType.String) return false;
            string verdict = (string)premise["verdict"], id = (string)premise["source"], quote = (string)premise["quote"];
            string claimed=(string)premise["claim"], intention=(string)JObject.Parse(input)["autonomousIntention"]??"";
            if(claimed.Length>Math.Max(200,intention.Length) || claimed.Length>0 && intention.IndexOf(claimed,StringComparison.Ordinal)<0)return false;
            if (id.Length > 40 || quote.Length > 200 || string.IsNullOrWhiteSpace((string)premise["reason"]) || ((string)premise["reason"]).Length > 160) return false;
            if (verdict == "no-factual-premise") { if (claimed != "" || id != "" || quote != "") return false; }
            else if (verdict == "supported")
            {
                if(string.IsNullOrWhiteSpace(claimed))return false;
                var facts = JObject.Parse(input); string evidence = null;
                if(id=="dialogue" && !string.IsNullOrWhiteSpace(quote) && facts["recentDialogue"] is JArray rows) {
                    var matches=rows.OfType<JObject>().Select((row,index)=>new { row,index,text=(string)row["quotedText"]??(string)row["content"] })
                        .Where(row=>row.text!=null && row.text.IndexOf(quote,StringComparison.Ordinal)>=0).ToArray();
                    if(matches.Length==1) { evidence=matches[0].text;premise["resolvedSource"]="dialogue:"+matches[0].index; }
                }
                else if(id=="last-user-utterance")evidence=(string)facts["currentUserText"];
                else if (id.StartsWith("dialogue:", StringComparison.Ordinal) && int.TryParse(id.Substring(9), out int index) &&
                    facts["recentDialogue"] is JArray dialogue && index >= 0 && index < dialogue.Count)
                    evidence = (string)dialogue[index]["quotedText"] ?? (string)dialogue[index]["content"];
                else if (id == "current-state") evidence = new JObject { ["currentFacts"] = facts["currentFacts"]?.DeepClone(),
                    ["execution"] = facts["execution"]?.DeepClone(), ["lastActionResults"] = facts["lastActionResults"]?.DeepClone() }.ToString(Formatting.None);
                else if (id == "current-image" && image && quote == "") evidence = "";
                if (evidence == null || (id != "current-image" && (string.IsNullOrWhiteSpace(quote) || evidence.IndexOf(quote, StringComparison.Ordinal) < 0))) return false;
            }
            else return false;
            value.Remove("autonomyPremise"); decision = value.ToString(Formatting.None); return true;
        }
        catch (Exception) { return false; }
    }
}
