using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// A words-only description of non-event clauses. No world facts or permission.
public static class SpeechActScopeProtocol
{
    public const string ModalityReminder = "\n最终按整句语法核对：‘无法确认是否P’只表明P未知，不等于‘没有P’，也不证明进行过失败的检查。没有历史证据时这种诚实的不确定回答可以成立。对未来用途的设想、愿望、建议和主观审美评价不需要已批准的执行计划或完成回执；不能把‘可能适合一起做某事’改读成‘承诺要执行某事’。相邻的真实事实前提仍须证据。speechActs及otherClaimTimes都是可错的语言分析，不得覆盖原句的否定、假设和作用域。社交邀请与任务执行分开：角色正在返回或已经实际到达，同时邀请用户靠近/拥抱/聊天，不自动转嫁任务。方向不同本身不足以判task-relation冲突；必须确有拒绝、替代、相互冲突的分工或违反用户限制。不能虚构到达来放行，也不能因附加邀请没说返回就推断放弃返回。听者为主语的祈使/条件邀请，不是角色承诺自己行动；靠近已经到达的角色也可表示再近一点，不自动矛盾。各Comparison用80字以内短对照，不复抄长ID或整句。";
    public const string ReviewContract = "Read modality in the WHOLE clause yourself. Wishes, imagined uses, suggestions, genuine questions, social invitations and subjective aesthetic judgments need no execution permission or receipt. An invitation is not a promise that either participant has acted. Retain separate factual premises. A hypothetical purpose modifier is not a promise. Explicit commitments and completed actions still need evidence, even if speechActs incorrectly calls them courtesy. A standalone acknowledgement without a concrete inspection object/method is not a separate tool event; otherClaims cannot turn it into one. An admission of uncertainty says neither that the questioned event occurred nor that it did not occur; no evidence for a prior event supports an honest unknown answer, not a failed inspection. independentSpeechClaim is an interpretation, not world evidence.\n" + ModalityReminder;
    private const string ReadingContract = "FIRST speechReading: briefly interpret the whole utterance (actor, modality/negation, asserted state versus event) before classifying it; no world facts. ";
    public const string Contract = ReadingContract + ScopeContract;
    private const string ScopeContract = "speechActs may be EMPTY. Mark exact WHOLE clauses by scope; assertion=an ordinary factual assertion, completed action or explicit commitment (retain its eventClaims/otherClaims), courtesy=standalone greeting/thanks/acknowledgement, NOT a polite ending on an action or promise; possibility=wish, suggestion or imagined use, NOT an explicit commitment; ongoing=current activity; uncertainty=explicit inability to establish WHETHER an event happened, NOT a claim the event failed. Include all dependent purpose/object modifiers in the same scope; a purpose phrase or a subordinate WHETHER clause alone is not an assertion. Keep separate concrete factual premises. Actual completed acts stay eventClaims, explicit commitments stay otherClaims, even when politely worded. Uncertain current observations remain factual. Courtesy/possibility are neither eventClaims nor otherClaims. Uncertainty stays otherClaims but never becomes a completed/failed inspection of the embedded event.\n";
    public const string MovementScopeContract = "Finally speechActs: exact whole clauses, max 6; assertion=factual assertion/completed event/explicit promise, courtesy=standalone acknowledgement, possibility=wish/suggestion/hypothetical, ongoing=current activity, uncertainty=unable to establish WHETHER an event occurred. Classify the whole utterance before splitting idioms. May be empty. Keep real events/commitments in eventClaims/otherClaims; courtesy/possibility are not execution assertions. Uncertainty stays otherClaims, never a failed inspection or denial of the embedded event. Retain separate factual premises.\n";
    public static void AddSchema(JObject schema, bool useMovementReading = false)
    {
        var field = new JObject { ["type"]="array",["maxItems"]=6,["items"]=new JObject {
            ["type"]="object",["additionalProperties"]=false,["required"]=new JArray("quote","kind"),
            ["properties"]=new JObject { ["quote"]=new JObject { ["type"]="string",["minLength"]=1,["maxLength"]=160 },
                ["kind"]=new JObject { ["type"]="string",["enum"]=new JArray("assertion","courtesy","possibility","ongoing","uncertainty") } } } };
        var properties=(JObject)schema["properties"];
        // Movement already has a whole-utterance scaffold. Keep it first rather
        // than generating a second reading that can anchor an isolated tense.
        var ordered = new JObject();
        if (useMovementReading) ordered["movementReading"] = properties["movementReading"].DeepClone();
        else ordered["speechReading"] = new JObject { ["type"]="string",["minLength"]=1,["maxLength"]=160 };
        if (!useMovementReading) ordered["speechActs"] = field;
        foreach (var property in properties.Properties())
            if (!ordered.ContainsKey(property.Name)) ordered[property.Name] = property.Value.DeepClone();
        if (useMovementReading) ordered["speechActs"] = field;
        schema["properties"] = ordered;
        ((JArray)schema["required"]).Insert(0,"speechActs");
        if (!useMovementReading) ((JArray)schema["required"]).Insert(0,"speechReading");
    }
    public static bool Validate(JToken acts,JToken events,JToken other,out string failure)
    {
        failure=null;
        if(acts==null)return true; // Legacy stored diagnostics; production schema requires it.
        if(!(acts is JArray rows)||rows.Count>6) { failure="speechActs:invalid";return false; }
        string Clause(string s)=>(s??"").Trim().TrimEnd('.', '。', '!', '！', '?', '？');
        foreach(var item in rows) {
            if(!(item is JObject row)||row.Count!=2||row["quote"]?.Type!=JTokenType.String||
                string.IsNullOrWhiteSpace((string)row["quote"])||((string)row["quote"]).Length>160||
                row["kind"]?.Type!=JTokenType.String || !new[]{"assertion","courtesy","possibility","ongoing","uncertainty"}.Contains((string)row["kind"])) {failure="speechActs:invalid";return false;}
            string span=Clause((string)row["quote"]);
            if((string)row["kind"]!="assertion" && (events as JArray)?.OfType<JObject>().Any(e=>span.Contains(Clause((string)e["quote"])))==true)
            {failure="speechActs:non-event-classified-as-ended-action";return false;}
            if ((string)row["kind"] == "courtesy" && (other as JArray)?.Values<string>().Any(c => Clause(c) == span) == true)
            {failure="speechActs:courtesy-classified-as-factual-claim";return false;}
            // Factual premises may coexist with a speculative whole clause. The
            // independent reviewer judges their modality; do not erase assertions.
        }
        return true;
    }
}
