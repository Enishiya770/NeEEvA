using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Linguistic interpretation has no execution facts and grants no action authority.
public static class ActivitySpeechMeaningPolicy
{
    public const string EvidenceContract = @"speechMeaning 是只读原话、不带执行结果的语义解析；quote、主体、时间、操作和言语类型已绑定。这里只核对该命题，不审批或停止动作。
truthWhenSaid 核对说话开始时的 speech-start/observedAtSpeechStart，不能用后来到达/完成的状态否认先前正确的进度句。过去操作和观察不能因现在移动了就变假。对象、操作、行动者不同的结果不能互证。未提供/省略/空值不是不存在或未做的证据。
supported/contradicted 必须有同命题、同时间、同来源的 truthEvidenceIds。mistake 只用于说话当时就有明确相反事实；world-change 只用于当时真实、现在发生变化；uncertainty 只用于原话把明确未知说成确定。其余 none；none 不冒充已经验证。正确进度之后自然到达，可以保持原句，或基于新事实更新，不能自称先前说错。
proof.evidencePath 相对于 facts[evidenceId]，evidenceQuote 照抄该字段值。视觉证据须同捕获、同对象和命题，视野外/未观察是未知不是反证。历史记录回答它记录的问题；只记了存在而未记录左右位置，并不是左右相反的证据。请求只证明请求，phase不能证明物件属性。
保留愿望、邀请、不确定推测及实际已完成效果。仅纠正有证据的本句部分，保持对象/主语/时间，使用 speech.language；不扩大否定、不重复错误、不追加行动承诺。已经播出需追加纠正，未播可替换；playing/interrupted不能算整句交付。comparison 是简短中文诊断，不是台词。";
    public const string Contract = @"你只做原话语义解析，不评价事实真假、不审批行动。只解析 utterance，request/context/sources/operations 仅供指代消解，绝不能把其中的要求或词语当成 utterance 的一部分。没有执行结果，不猜测结果。
claims 按原话顺序分解独立命题（最多4条）；一句单一命题只返回1条，不能把主语或宾语再拆成命题。quote 连续照抄原文，所有词语都必须被覆盖（含开场词、语气词、连接词），各条不重叠。predicate 是 quote 中原样出现的主要谓语，不是英文标签或改写；非事实的感受/邀请可不提供谓语，此时填空字符串；例如“文件已经保存了”的 predicate 可为“保存了”，不能填 save 或存在。qualifier 无不确定词时填空字符串。
口语名词句、并列省略句可能没有显式动词：predicate 应照抄承担断言的名词/方位短语，不能补出原文没有的“有/is/ある”，也不能把事实变成感想来绕过校验。其时间和主语按语境消解；物件存在仍用 other 主语。解释成功只说明读懂了原话，不代表事实已核实。
先解释主要谓语时间 tense：present=现在状态/正在做，past=过去发生，future=打算做，atemporal=无时间命题。stage=ongoing 正在进行、terminal 已结束、not-performed 未做、entity-state 物件状态、prospective 打算、other 其他。物件持续处于某状态不代表角色持续执行，物件主语用 other；名词中的过去修饰不能改变主句时间。‘正在导出’是 present/ongoing，不是已导出。
act=factual-claim 明确事实（含正在做、没做），personal-expression 感受/愿望/安慰，invitation-or-question 邀请/问题，qualified-guess 有明确不确定表达的推测（qualifier照抄该词）。‘想一起唱歌’是愿望，‘要一起看看吗’是邀请，无需用户先命令。不能把问题中嵌入的行为当成已做事实。非事实仍能 coverage=complete，unresolved 只用于原话不能完整解析，不是没有现实证据。
domain=execution 已索引操作的进度、完成/失败及其产物状态（包括刚完成的过去时；文件、图片、检索结果就绪不属 visual）；history 另一个过去经历/以前观察，不是正在报告的已索引任务结果；visual 当前图像内容；request 用户指令；other 其他。愿望/邀请用 other。source=earlier-observation 以前观察；current-writer-image 仅 currentImage 提供的实际观察图像，不是生成/编辑任务的产物状态；not-applicable 非视觉（包括动作）；uncertain 来源不明。execution 使用 not-applicable；历史经历无论有没有记录都属 history。问以前看到什么应查以前来源，不能根据现在视角回答。
previousAttempt 是被拒输出和具体原因，未产生纠正；仅修复该错误，仍须覆盖整个 utterance，不能删掉连接词或补入 context。
operation 标识本句谈论哪个操作，肯定、否定、失败、尚未执行都仍需绑定该操作，与操作是否成功无关。只从 operations 选择；没有唯一对应项用 unresolved；not-applicable 仅用于非操作事实/感想/新意愿。不能为了匹配旧操作改写原话。subject=character/user/other/unspecified：utterance 是角色对用户说的话，所以其中第一人称为 character、第二人称为 user；request 则是用户对角色说的话，不能挪用 request 的说话人或主语。只返回完整 JSON，无纠正、执行或结算。";
    static JObject Choice(params string[] values)=>new JObject{["type"]="string",["enum"]=new JArray(values)};
    static JObject Text(int n)=>new JObject{["type"]="string",["maxLength"]=n};
    static JObject Row(JObject fields)=>new JObject{["type"]="object",["additionalProperties"]=false,["required"]=new JArray(fields.Properties().Select(p=>p.Name)),["properties"]=fields};
    public static JObject Input(JObject frame)
    {
        var unit=(JObject)((JArray)frame["speech"])[0];var scopes=frame["evidenceScopes"] as JObject??new JObject();
        var request=frame["facts"]?["request"];var requestText=request is JObject r?r["text"]:request?.Type==JTokenType.String?request:null;
        var observed=ActivityEvidencePolicy.FrozenSpeech(unit,frame,true)?["formalObservation"] as JObject??frame["facts"]?["observation"]?["formalSource"] as JObject;
        var image=(bool?)observed?["formalPixelsProvided"]==true && !string.IsNullOrEmpty((string)observed["captureId"])?
            new JObject{["captureId"]=observed["captureId"],["scope"]="Image offered for this utterance's visual observation, not a task's generated artifact."}:null;
        var ops=(frame.SelectToken("reviewOperation.availableOperations") as JArray)?.DeepClone() as JArray;
        if(ops==null)ops=new JArray(scopes.Properties().Where(p=>p.Value["binding"] is JObject && (string)p.Value["domain"]=="execution")
            .GroupBy(p=>ActivityEvidencePolicy.Stamp(p.Value["binding"])).Select(g=>new JObject{
                ["source"]=g.First().Name,["claimKey"]=(string)g.First().Value["binding"]["operation"]+":"+g.First().Name,
                ["entity"]=g.First().Value["binding"]["entity"],["binding"]=g.First().Value["binding"]?.DeepClone()}));
        foreach(var op in ops.OfType<JObject>())if(op["binding"]==null)op["binding"]=scopes[(string)op["source"]]?["binding"]?.DeepClone()??frame["facts"]?[(string)op["source"]]?["binding"]?.DeepClone();
        return new JObject{["purpose"]="speech-meaning",["owner"]=frame["owner"]?.DeepClone(),
            ["speechId"]=unit["id"], ["version"]=unit["version"],["utterance"]=unit["text"],
            ["request"]=requestText?.DeepClone(),["currentImage"]=image,
            ["historyQuestionSources"]=ActivityClaimSourcePolicy.QuestionContext(frame)?["retainedSources"]?.DeepClone(),
            ["context"]=unit["linguisticContext"]?.DeepClone(),["operations"]=ops,
            ["sources"]=new JArray((frame["facts"] as JObject??new JObject()).Properties().Where(p=>p.Name.StartsWith("recalled:")).Select(p=>new JObject{
                ["id"]=p.Name,["question"]=p.Value["question"]??((string)p.Value["questionSource"]=="request"?requestText:null),
                ["time"]=p.Value["capturedAtUtc"], ["entity"]=p.Value["entityId"]})),
            ["previousAttempt"]=frame["previousAttempt"]?.DeepClone(),["monitorRequestId"]=frame["monitorRequestId"]?.DeepClone()};
    }
    public static string Key(JObject frame)
    {
        var input=Input(frame);input.Remove("monitorRequestId");input.Remove("previousAttempt");return ActivityEvidencePolicy.Stamp(input);
    }
    public static JObject Schema(JObject input)
    {
        var ops=((JArray)input["operations"]).Select(o=>(string)o["claimKey"]).Concat(new[]{"not-applicable","unresolved"}).Distinct().ToArray();
        var claim=Row(new JObject{["quote"]=Text(480),["predicate"]=Text(120),["tense"]=Choice("present","past","future","atemporal"),
            ["act"]=Choice("factual-claim","personal-expression","invitation-or-question","qualified-guess"),
            ["qualifier"]=Text(80),["subject"]=Choice("character","user","other","unspecified"),
            ["domain"]=Choice("execution","history","visual","request","other"),
            ["stage"]=Choice("ongoing","terminal","not-performed","entity-state","prospective","other"),
            ["operation"]=Choice(ops),["source"]=input["currentImage"] is JObject?Choice("earlier-observation","current-writer-image","not-applicable","uncertain"):Choice("earlier-observation","not-applicable","uncertain")});
        var branches=new JArray();
        foreach(string domain in new[]{"execution","history","visual","request","other"}) {
            var branch=(JObject)claim.DeepClone();var fields=(JObject)branch["properties"];
            fields["domain"]=Choice(domain);
            fields["source"]=domain=="execution"||domain=="request"||domain=="other"?Choice("not-applicable"):
                domain=="history"?Choice("earlier-observation","not-applicable","uncertain"):
                input["currentImage"] is JObject?Choice("current-writer-image","uncertain"):Choice("uncertain");
            if(domain=="execution")fields["operation"]=Choice(ops.Where(x=>x!="not-applicable").ToArray());
            branches.Add(branch);
        }
        return Row(new JObject{["coverage"]=Choice("complete","unresolved"),["claims"]=new JObject{["type"]="array",["minItems"]=1,["maxItems"]=4,["items"]=new JObject{["anyOf"]=branches}}});
    }
    public static bool TryRead(string raw,JObject input,out JObject meaning)
    {
        meaning=null;
        try {
            var value=ActivityEvidencePolicy.Parse(raw);var rows=value["claims"] as JArray;
            if((string)value["coverage"]!="complete"||rows==null||rows.Count<1||rows.Count>4||rows.OfType<JObject>().Count()!=rows.Count)return false;
            string text=(string)input["utterance"]??"";int offset=0;
            foreach(var row in rows.OfType<JObject>()) {
                string quote=(string)row["quote"],predicate=(string)row["predicate"];
                int at=string.IsNullOrWhiteSpace(quote)?-1:text.IndexOf(quote,offset,StringComparison.Ordinal);
                bool nonTemporalExpression=new[]{"personal-expression","invitation-or-question"}.Contains((string)row["act"]);
                if(at<0||predicate==null||!quote.Contains(predicate)||string.IsNullOrWhiteSpace(predicate)&&!nonTemporalExpression)return false;
                // All lexical content must be covered; skipped punctuation is harmless.
                if(text.Substring(offset,at-offset).Any(char.IsLetterOrDigit))return false;
                offset=at+quote.Length;
                if((string)row["act"]=="qualified-guess" && (string.IsNullOrWhiteSpace((string)row["qualifier"])||!quote.Contains((string)row["qualifier"])))return false;
                if((string)row["act"]=="factual-claim" && (string)row["tense"]=="atemporal")return false;
                if((string)row["tense"]=="past"&&(string)row["source"]=="current-writer-image"&&
                    (input["historyQuestionSources"] as JArray)?.Count>0)return false;
                if(!new[]{"present","past","future","atemporal"}.Contains((string)row["tense"]) ||
                    !new[]{"factual-claim","personal-expression","invitation-or-question","qualified-guess"}.Contains((string)row["act"]) ||
                    !new[]{"character","user","other","unspecified"}.Contains((string)row["subject"]) ||
                    !new[]{"execution","history","visual","request","other"}.Contains((string)row["domain"]) ||
                    !new[]{"ongoing","terminal","not-performed","entity-state","prospective","other"}.Contains((string)row["stage"]) ||
                    !new[]{"earlier-observation","current-writer-image","not-applicable","uncertain"}.Contains((string)row["source"]))return false;
                if((string)row["stage"]=="ongoing" && new[]{"factual-claim","qualified-guess"}.Contains((string)row["act"]) && (string)row["tense"]!="present")return false;
                if(new[]{"personal-expression","invitation-or-question"}.Contains((string)row["act"])) {
                    row["domain"]="other";row["source"]="not-applicable";row["operation"]="not-applicable";
                }
                if((string)row["act"]!="qualified-guess")row["qualifier"]="";
                var operation=((JArray)input["operations"]).OfType<JObject>().FirstOrDefault(o=>(string)o["claimKey"]==(string)row["operation"]);
                if(operation==null && !new[]{"not-applicable","unresolved"}.Contains((string)row["operation"]))return false;
                if((string)row["act"]=="factual-claim"&&(string)row["domain"]=="execution"&&(string)row["operation"]=="not-applicable")return false;
                // An indexed execution receipt is not a camera observation. An
                // unbound historical claim must not lose its provenance this way.
                if((string)row["domain"]=="execution"&&(string)row["source"]!="not-applicable") {
                    if(operation?["binding"] is JObject && (string)row["source"]=="earlier-observation")row["source"]="not-applicable";
                    else return false;
                }
                if((string)row["source"]=="earlier-observation")row["domain"]="history";
                row["operationBinding"]=operation?.DeepClone();
            }
            if(text.Substring(offset).Any(char.IsLetterOrDigit))return false;
            meaning=value;return true;
        }catch(Exception){return false;}
    }
    public static string Rejection(string raw,JObject input)
    {
        try {
            var parsed=ActivityEvidencePolicy.Parse(raw);var rows=parsed["claims"] as JArray;
            if(rows==null)return "Missing claims array.";
            string text=(string)input["utterance"]??"";int offset=0;
            foreach(var row in rows.OfType<JObject>()) {
                string quote=(string)row["quote"]??"",predicate=(string)row["predicate"]??"";
                int at=string.IsNullOrEmpty(quote)?-1:text.IndexOf(quote,offset,StringComparison.Ordinal);
                if(at<0)return "quote must be ordered, nonoverlapping verbatim utterance text, not request/context.";
                if((string)row["tense"]=="past"&&(string)row["source"]=="current-writer-image"&&
                    (input["historyQuestionSources"] as JArray)?.Count>0)return ActivityClaimSourcePolicy.Repair;
                string gap=text.Substring(offset,at-offset);
                if(gap.Any(char.IsLetterOrDigit))return "quote omitted these exact words: "+gap+". Include them in the adjacent clause; do not drop conjunctions or prefaces.";
                if(!quote.Contains(predicate)||string.IsNullOrWhiteSpace(predicate)&&!(new[]{"personal-expression","invitation-or-question"}.Contains((string)row["act"])))return "predicate must copy actual words from its quote, not an invented or translated verb. For an elliptical nominal clause without a written verb, copy the asserting noun/location phrase verbatim; keep the factual meaning and resolve its tense/subject from context. Nonfactual expressions/invitations may omit the predicate. Do not invent existence verbs, change a fact into a feeling, or leave a factual predicate empty.";
                if((string)row["act"]=="factual-claim"&&(string)row["domain"]=="execution"&&(string)row["operation"]=="not-applicable")return "This is an execution assertion. Bind the operation it asserts or denies to the index; negation, failure and not-yet-done still refer to that operation. Use unresolved only if no unique indexed operation matches; do not use not-applicable merely because the sentence denies performing it.";
                if((string)row["domain"]=="execution"&&(string)row["source"]!="not-applicable")return "Execution and observation source conflict. Re-read the utterance's subject and time: past experiences belong to history; a current task operation uses execution/not-applicable. Do not discard historical provenance or transfer the request speaker into the character's utterance.";
                offset=at+quote.Length;
            }
            if(text.Substring(offset).Any(char.IsLetterOrDigit))return "Uncovered utterance tail: "+text.Substring(offset);
            return "Return complete literal coverage, valid classifications and exact qualifiers. No outcome facts are available or required. Unrelated new wishes need no indexed operation.";
        }catch(Exception){return "Invalid JSON; return only schema-conforming literal claims.";}
    }
    public static IEnumerable<JObject> Frames(JObject frame,JObject meaning)
    {
        int index=0;
        foreach(var row in ((JArray)meaning["claims"]).OfType<JObject>()) {
            var selected=(JObject)frame.DeepClone();selected.Remove("reviewOperation");
            selected["speechMeaning"]=row.DeepClone();selected["claimIndex"]=index++;
            if((string)row["domain"]=="visual")selected["visualClaimGroup"]=new JArray(((JArray)meaning["claims"]).OfType<JObject>()
                .Where(c=>(string)c["domain"]=="visual"&&(string)c["act"]=="factual-claim"&&(string)c["source"]=="current-writer-image")
                .Select(c=>new JObject{["quote"]=c["quote"],["source"]=c["source"]}));
            if(row["operationBinding"] is JObject binding && binding["binding"] is JObject)
                selected["reviewOperation"]=new JObject{["evidenceId"]=binding["source"],["binding"]=binding["binding"].DeepClone(),
                    ["claimKey"]=binding["claimKey"],["availableOperations"]=new JArray(binding.DeepClone())};
            yield return selected;
        }
    }
    public static void ProjectFacts(JObject frame)
    {
        if(!(frame["speechMeaning"] is JObject meaning))return;
        string domain=(string)meaning["domain"];var facts=frame["facts"] as JObject;var scopes=frame["evidenceScopes"] as JObject;
        if(facts==null)return;
        // Provenance must survive even a wrong initial semantic domain. Otherwise
        // projection could erase the very source needed to reject that reading.
        if(ActivityClaimSourcePolicy.QuestionContext(frame) is JObject sourceContext)frame["claimSourceContext"]=sourceContext.DeepClone();
        // One unit can contain several assertions. Supply only this assertion's
        // image observation under the protocol's canonical speech source key.
        string speechId=(string)(frame["speech"] as JArray)?.FirstOrDefault()?["id"];
        JObject visual=null,capability=null;
        foreach(var field in facts.Properties().Where(p=>p.Name.StartsWith("visual:")).ToArray()) {
            if((string)field.Value["speechId"]==speechId && (string)field.Value["claimQuote"]==(string)meaning["quote"]) {
                visual=field.Value.DeepClone() as JObject;capability=scopes?[field.Name]?.DeepClone() as JObject;
            }
            facts.Remove(field.Name);scopes?.Remove(field.Name);
        }
        if(visual!=null&&domain=="visual") {facts["visual:"+speechId]=visual;if(scopes!=null)scopes["visual:"+speechId]=capability;}
        foreach(var field in facts.Properties().ToArray()) {
            bool keep=field.Name=="request"||field.Name=="constraints"||field.Name=="dialogue" ||
                domain=="history"&&(field.Name=="historyAnswers"||field.Name=="input-resolution"||field.Name.StartsWith("recalled:")) ||
                domain=="visual"&&(field.Name=="observation"||field.Name.StartsWith("visual:")) ||
                domain=="execution"&&(field.Name=="execution"||field.Name=="speech-start"||field.Name=="planningFailure"||field.Name.StartsWith("result:")||field.Name.StartsWith("action:"));
            if(!keep){facts.Remove(field.Name);scopes?.Remove(field.Name);}
        }
        foreach(var unit in (frame["speech"] as JArray??new JArray()).OfType<JObject>()) {
            unit.Remove("linguisticContext"); // Frozen interpretation already contains resolved referents.
            if(domain!="execution")foreach(string key in new[]{"observedAtCreation","observedAtSpeechStart"}) {
                if(unit[key] is JObject frozen)unit[key]=new JObject{["formalObservation"]=domain=="visual"?frozen["formalObservation"]?.DeepClone():null};
            }
        }
    }
    public static string KnownOngoingTruth(JObject frame)
    {
        var m=frame["speechMeaning"] as JObject;
        if((string)m?["stage"]!="ongoing"||(string)m["subject"]!="character"||(string)m["domain"]!="execution")return null;
        var binding=m.SelectToken("operationBinding.binding");var scope=frame["evidenceScopes"]?["speech-start"]?["binding"];
        if(binding==null||!ActivityEvidencePolicy.Equal(binding,scope))return null;
        var start=frame["facts"]?["speech-start"];
        string phase=(string)start?.SelectToken("body.execution.phase")??(string)start?["phase"]??(string)start?["status"];
        return new[]{"moving","running","executing","follow-moving","follow-waiting","streaming"}.Contains(phase)?"supported":null;
    }
    public static void BindSchema(JObject schema,JObject frame)
    {
        if(!(frame["speechMeaning"] is JObject m))return;
        if(schema["anyOf"] is JArray roots){
            foreach(var branch in roots.OfType<JObject>().ToArray()) {
                BindSchema(branch,frame);
                if(!PruneSchema(branch))branch.Remove();
            }
            return;
        }
        var fields=schema["properties"] as JObject;if(fields==null)return;
        fields["claimQuote"]=Choice((string)m["quote"]);fields["mainPredicateQuote"]=Choice((string)m["predicate"]);
        fields["predicateTense"]=Choice((string)m["tense"]);fields.Remove("claimTime");
        fields["claimSubject"]=Choice((string)m["subject"]);fields["perceptionSource"]=Choice((string)m["source"]);
        if(m.SelectToken("operationBinding.binding.entity") is JValue entity && (string)m["domain"]=="execution")fields["claimEntity"]=Choice((string)entity);
        if(fields["claimOperation"]!=null)fields["claimOperation"]=Choice((string)m["operation"]);
        bool unresolvedOperation=(string)m["act"]=="factual-claim"&&(string)m["domain"]=="execution"&&m.SelectToken("operationBinding.binding") is not JObject;
        var variants=fields["grounding"]?["anyOf"] as JArray;
        if(variants==null && fields["grounding"] is JObject single) {
            variants=new JArray(single.DeepClone());fields["grounding"]=new JObject{["anyOf"]=variants};
        }
        if(variants!=null) {
            string truth=unresolvedOperation?"unknown":KnownOngoingTruth(frame);
            foreach(var row in variants.OfType<JObject>().ToArray()) {
                var g=(JObject)row["properties"];
                bool Allows(string key,string value)=>(g[key]?["enum"] as JArray??new JArray()).Values<string>().Contains(value);
                if(!Allows("speechAct",(string)m["act"]) || !Allows("claimDomain",(string)m["domain"]) || truth!=null&&!Allows("truthWhenSaid",truth)) {row.Remove();continue;}
                g["speechAct"]=Choice((string)m["act"]);g["claimDomain"]=Choice((string)m["domain"]);g["qualificationQuote"]=Choice((string)m["qualifier"]??"");
                if(truth!=null){g["truthWhenSaid"]=Choice(truth);g["truthEvidenceIds"]=unresolvedOperation?
                    new JObject{["type"]="array",["maxItems"]=0,["items"]=Text(1)}:
                    new JObject{["type"]="array",["minItems"]=1,["maxItems"]=1,["items"]=Choice("speech-start")};}
            }
        }
        if((string)m["act"]!="factual-claim"||unresolvedOperation)fields["speech"]=Row(new JObject{["issue"]=Choice("none")});
        else if(KnownOngoingTruth(frame)=="supported" && fields["speech"]?["anyOf"] is JArray interventions) {
            foreach(var branch in interventions.OfType<JObject>().ToArray()) {
                var allowed=branch["properties"]?["issue"]?["enum"] as JArray;
                if(allowed==null)continue;
                var retained=allowed.Values<string>().Where(x=>x=="none"||x=="world-change").ToArray();
                if(retained.Length==0)branch.Remove();else branch["properties"]["issue"]["enum"]=new JArray(retained);
            }
        }
        fields["claimKind"]=Choice((string)m["stage"]=="entity-state"?"entity-state":(string)m["domain"]=="execution"?"action-event":"other");
        schema["required"]=new JArray(fields.Properties().Select(p=>p.Name));
        BindTruthAndIntervention(schema,(string)m["tense"]);
    }
    // Binding a literal reading can make an old repair alternative impossible.
    // Remove that alternative instead of emitting an empty native-schema enum.
    public static bool PruneSchema(JObject schema)
    {
        if(schema["enum"] is JArray values && values.Count==0)return false;
        if(schema["anyOf"] is JArray branches) {
            foreach(var branch in branches.OfType<JObject>().ToArray())if(!PruneSchema(branch))branch.Remove();
            if(branches.Count==0)return false;
        }
        if(schema["properties"] is JObject fields)
            foreach(string key in (schema["required"] as JArray??new JArray()).Values<string>())
                if(fields[key] is JObject field&&!PruneSchema(field))return false;
        return true;
    }
    static void BindTruthAndIntervention(JObject schema,string tense)
    {
        var fields=schema["properties"] as JObject;
        if(!(fields?["grounding"]?["anyOf"] is JArray groundings) || !(fields["speech"]?["anyOf"] is JArray speech))return;
        var choices=new JArray();
        foreach(string truth in new[]{"supported","contradicted","unknown"}) {
            var gs=new JArray();
            foreach(var g in groundings.OfType<JObject>()) {
                if(!((JArray)g["properties"]["truthWhenSaid"]["enum"]).Values<string>().Contains(truth))continue;
                var row=(JObject)g.DeepClone();row["properties"]["truthWhenSaid"]=Choice(truth);gs.Add(row);
            }
            if(gs.Count==0)continue;
            var ss=new JArray();
            foreach(var branch in speech.OfType<JObject>()) {
                var allowed=((JArray)branch["properties"]["issue"]["enum"]).Values<string>().Where(x=>x=="none" || (truth!="supported" || tense=="present") &&
                    x==(truth=="supported"?"world-change":truth=="contradicted"?"mistake":"uncertainty")).ToArray();
                if(allowed.Length==0)continue;
                var row=(JObject)branch.DeepClone();row["properties"]["issue"]=Choice(allowed);ss.Add(row);
            }
            if(ss.Count==0)continue;
            var shape=(JObject)schema.DeepClone();shape["properties"]["grounding"]=new JObject{["anyOf"]=gs};
            shape["properties"]["speech"]=new JObject{["anyOf"]=ss};choices.Add(shape);
        }
        if(choices.Count>0){schema.RemoveAll();schema["anyOf"]=choices;}
    }
    public static bool Matches(JObject frame,JObject verdict)
    {
        if(!(frame["speechMeaning"] is JObject m))return true;
        string time=new Dictionary<string,string>{{"present","current-state"},{"past","past-event"},{"future","prospective"},{"atemporal","not-factual"}}[(string)m["tense"]];
        if((string)verdict["claimQuote"]!=(string)m["quote"] || (string)verdict["claimTime"]!=time ||
            (string)verdict["speechAct"]!=(string)m["act"]||(string)verdict["claimDomain"]!=(string)m["domain"] ||
            (string)verdict["claimSubject"]!=(string)m["subject"] || (string)verdict["perceptionSource"]!=(string)m["source"] ||
            frame["reviewOperation"]!=null&&(string)verdict["claimOperation"]!=(string)m["operation"])return false;
        if((string)m["act"]=="factual-claim"&&(string)m["domain"]=="execution"&&m.SelectToken("operationBinding.binding") is not JObject)
            return (string)verdict["truthWhenSaid"]=="unknown"&&(string)verdict["speech"]?["issue"]=="none";
        string truth=KnownOngoingTruth(frame);
        return truth==null||(string)verdict["truthWhenSaid"]==truth;
    }
}
