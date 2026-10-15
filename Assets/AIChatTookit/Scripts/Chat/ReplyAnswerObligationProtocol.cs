using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// Same-turn interpretation carries a reporting obligation, never action authority.
public static class ReplyAnswerObligationProtocol
{
    public const string ReviewContract = "你是复合请求的发声事实审核器。只输出给定schema。先按answerCoverage核对每个独立问题和台词中的逐字答案，再核对动作结果，最后给verdict。" +
        "问题有自己的对象、行动者和时间：询问过去是否看到/保存，与当前开关/播放成功是两件事。‘不能确认是否发生’只表示不知道，既不否定过去事件，也不声称检查尝试失败。过去证据未提供时，诚实的explicit-unknown就是有效答案，不必编造过去结果；当前操作成功不能反驳这种过去不确定性。" +
        "现在仍在做、已经完成、失败、未尝试及未来承诺必须分别核对当前真实receipt；不能用指令或台词当完成证据。otherClaims逐项保留原句的否定/假设/时间范围，禁止改写成更强断言再拒绝。额外事实、原因、方法及明确未来执行承诺仍须证据/有效计划。" +
        "targetComparison/targetConsistency核对台词实际说出的目标与当前已绑定实体，使用已有语义匹配及同实体回执。中性指代或省略修饰语可以指当前对象；不同名称不自动构成矛盾，不能把用户原问的全部描述强加给台词再误判。明确相冲突的位置/属性或另一实体仍须拒绝。taskRelation只核对角色和用户的行动关系，不能因独立问题未提移动而判反转。actualLanguage/resultReport保持独立抽取的分类。纯礼貌、主观愿望和假设无需动作权限。" +
        "对真正不支持/矛盾的具体声称给inconsistent及逐字conflictQuote；身份不明给uncertain；正确结果加诚实的不确定回答给consistent。factScope按实际证据来源填写。eventBindings若存在，仍按其独立合同核对每个事件的行动者、操作、对象、状态和时间，不放过错误归属。\n";
    public const string DeliveryCriterion = "Settle the answer, not the external event: a completed spoken answer OR an honest explicit inability to establish the asked fact is sufficient. A current action result alone is not an answer to a question about earlier events. Do not require unavailable knowledge or repeat an already delivered unknown answer.";
    public const string Contract = "answerObligations preserves questions independently identified in this complete turn. For each, answerCoverage must retain questionQuote and quote the EXACT publicSpeech span answering that question, with status answered, explicit-unknown, or omitted. Compare object, actor and TIME: doing something now does not answer whether it was seen/done earlier or why it failed. A natural implicit answer counts only if it actually resolves the question. Honest lack of evidence can be explicit-unknown; a courtesy opener, action promise or unrelated result is omitted. Do not invent facts or demand successful execution to answer a question.\n";
    public static JArray FromSemantics(string request,JObject semantics)
    {
        var result=new JArray();
        string quote=(string)semantics?.SelectToken("currentTurnEvidence.answerQuote");
        if((string)semantics?["requestKind"]=="mixed" && !string.IsNullOrWhiteSpace(quote) &&
            !string.IsNullOrEmpty(request) && request.IndexOf(quote,StringComparison.Ordinal)>=0)
            result.Add(new JObject { ["questionQuote"]=quote,["scope"]="Same-turn question; action completion alone does not discharge it. No action authority." });
        return result;
    }
    public static JArray Read(JObject input) => input?["answerObligations"] as JArray ??
        input?.SelectToken("sceneActionFacts.answerObligations") as JArray ??
        input?.SelectToken("taskSequence.answerObligations") as JArray ?? new JArray();
    public static JObject DeliveryResult(JObject question) => WorkResultDeliveryProtocol.Result(
        "independent-answer", "answer-required", new JObject { ["questionQuote"] = question["questionQuote"]?.DeepClone(), ["deliveryCriterion"] = DeliveryCriterion });
    public static void AddReviewSchema(JObject schema,JObject input)
    {
        var questions=Read(input);if(questions.Count==0)return;
        schema["properties"]["answerCoverage"]=new JObject { ["type"]="array",["minItems"]=questions.Count,["maxItems"]=questions.Count,
            ["items"]=new JObject { ["type"]="object",["additionalProperties"]=false,["required"]=new JArray("questionQuote","status","answerQuote"),
                ["properties"]=new JObject { ["questionQuote"]=new JObject { ["type"]="string",["enum"]=new JArray(questions.Select(q=>(string)q["questionQuote"])) },
                    ["status"]=new JObject { ["type"]="string",["enum"]=new JArray("answered","explicit-unknown","omitted") },
                    ["answerQuote"]=new JObject { ["type"]="string",["maxLength"]=300 } } } };
        ((JArray)schema["required"]).Add("answerCoverage");
        var original=(JObject)schema["properties"];
        schema["properties"]=new JObject(new JProperty("answerCoverage",original["answerCoverage"].DeepClone()),
            original.Properties().Where(p=>p.Name!="answerCoverage").Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
    }
    public static string Reject(JObject input,JObject review)
    {
        var questions=Read(input);if(questions.Count==0)return null;
        var rows=review["answerCoverage"] as JArray;string speech=(string)input["publicSpeech"]??"";
        if(rows==null||rows.Count!=questions.Count)return "answer-coverage-missing";
        foreach(var question in questions) {
            var matches=rows.OfType<JObject>().Where(r=>(string)r["questionQuote"]==(string)question["questionQuote"]).ToArray();
            if(matches.Length!=1)return "answer-coverage-unbound";
            var row=matches[0];string answer=(string)row["answerQuote"];
            if(!new[]{"answered","explicit-unknown"}.Contains((string)row["status"]) ||
                string.IsNullOrWhiteSpace(answer)||speech.IndexOf(answer,StringComparison.Ordinal)<0)return "independent-question-unanswered";
            if ((string)row["status"]=="explicit-unknown" &&
                !(input.SelectToken("independentSpeechClaim.speechActs") as JArray ?? new JArray()).OfType<JObject>().Any(a =>
                    (string)a["kind"]=="uncertainty" && !string.IsNullOrWhiteSpace((string)a["quote"]) &&
                    (answer.Contains((string)a["quote"]) || ((string)a["quote"]).Contains(answer))))
                return "answer-uncertainty-not-in-spoken-words";
        }
        return null;
    }
}
