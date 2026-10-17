using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Read-only interpretation of one retained image. No navigation or identity authority.
public static class SceneRecallProtocol
{
    public const string Contract = "按captureRole检查这一张带来源的图片：historical是旧图召回；current-verification是转向后重新采集的图，仅确认现在能看到的问题所指对象，不能反推以前已经看到了。回答完整question中的对象、时间和相对关系。图片中的编号仅属于本次captureId；场景名称仅为不可靠提示。" +
        "questionInterpretation是根据完整对话消解指代的模型解释，不替代原话；historicalTargetHint只说明旧图中的目标线索。结合原话解决‘那个/刚才那里’的省略，不能把旧图结论当作新图已确认，含糊则unresolved。" +
        "supported要求目标的外观及必要空间关系均有像素依据，只有同色/近似外形不能替代相对位置、数量或大小等限定；先finding说明证据和缺口，再选status。not-confirmed表示本图不能确认所指目标，不能因已经成功转头就选supported。" +
        "先逐项简短描述确实可辨认的候选（最多6项，每项尽量20字内），finding仅说明目标的关键视觉关系及缺口（最多160字符），然后判断问题所指目标在图中是否可确认，支持则引用对应referenceId，否则留空并解释缺口。" +
        "status=supported只说明回看这张图可以确认，不代表角色此前曾注意到/辨认过它；not-confirmed不是不存在或未曾见过；unresolved用于像素、指代或范围不足。" +
        "candidates的referenceId用于回答，candidateId/marker对应图中编号；单目标核对可能把原c6单独重绘为marker 1，必须依此映射。描述编号轮廓内的物件本身，不能把附近灯写成承载它的架子，也不能把相邻盆栽的名称赋给灯。imagePoint/pixelBounds是原图左上角坐标辅助定位，不代表语义。" +
        "可以根据视觉和关系理解不同语言的名称，不能仅凭名称认物。保留不确定性。当前不可见、自身移动不证明物件移动；旧图不能证明现在的状态。无动作、无承诺、无新物件ID。";

    public static string SourceContract(JObject input) => (string)input["captureRole"]=="historical"
        ? "本次仅检查一张保留的过去画面。captureRole=historical由采集系统给出，这张图不是现在/转身后的视野。用户说刚才或转身前，正是在问旧观察；当前已转向不改变这张旧图的采集时态。只要目标及所需关系在这张旧图中清楚成立就可supported，不需要证明当前仍可见，也不需要证明此前已主动辨认。不能虚构转身前后的来源关系。\n"
        : "本次仅检查执行器转向后取得的新画面，captureRole=current-verification。历史提示仅消解目标，是否再次看见必须由本张新图支持。完成转向本身不是目标可见的证明。\n";

    public static JObject Schema(JObject input)
    {
        JObject Text(int n) => new JObject { ["type"]="string",["maxLength"]=n };
        var ids=new JArray((input["candidates"] as JArray ?? new JArray()).OfType<JObject>().Select(x=>(string)x["referenceId"]));
        var selection=(JArray)ids.DeepClone();selection.Add("");
        var common=new JObject { ["type"]="object",["additionalProperties"]=false,
            ["required"]=new JArray("captureRole","captureId","observations","status","referenceId","finding"),["properties"]=new JObject {
                ["captureRole"]=new JObject{["type"]="string",["enum"]=new JArray((string)input["captureRole"])},
                ["captureId"]=new JObject{["type"]="string",["enum"]=new JArray((string)input["captureId"])},
                ["observations"]=new JObject { ["type"]="array",["maxItems"]=6,["items"]=new JObject {
                    ["type"]="object",["additionalProperties"]=false,["required"]=new JArray("referenceId","description"),
                    ["properties"]=new JObject { ["referenceId"]=ids.Count==0?new JObject{["type"]="string",["enum"]=new JArray("")}:new JObject{["type"]="string",["enum"]=ids},["description"]=Text(160) }} },
                ["finding"]=Text(160),["status"]=new JObject{["type"]="string",["enum"]=new JArray("supported","not-confirmed","unresolved")},
                ["referenceId"]=new JObject{["type"]="string",["enum"]=selection}
            }};
        var supported=(JObject)common.DeepClone();supported["properties"]["status"]["enum"]=new JArray("supported");
        supported["properties"]["referenceId"]["enum"]=ids.DeepClone();
        common["properties"]["status"]["enum"]=new JArray("not-confirmed","unresolved");
        common["properties"]["referenceId"]["enum"]=new JArray("");
        return ids.Count==0?common:new JObject{["anyOf"]=new JArray(supported,common)};
    }

    public static bool TryRead(string json,JObject input,out JObject result,out string reason)
    {
        result=null;reason="recall-invalid-output";
        try {
            var r=JObject.Parse(json,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
            var ids=(input["candidates"] as JArray??new JArray()).OfType<JObject>().Select(x=>(string)x["referenceId"]).ToArray();
            string selected=(string)r["referenceId"],status=(string)r["status"];
            if(r.Count!=6 || (string)r["captureRole"]!=(string)input["captureRole"] || (string)r["captureId"]!=(string)input["captureId"] ||
                !new[]{"supported","not-confirmed","unresolved"}.Contains(status) ||
                r["finding"]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)r["finding"]) || ((string)r["finding"]).Length>160 ||
                !(r["observations"] is JArray notes) || notes.Count>6 ||
                (status=="supported" ? !ids.Contains(selected) : selected!=""))return false;
            foreach(var note in notes) if(!(note is JObject n) || n.Count!=2 || !ids.Contains((string)n["referenceId"]) ||
                n["description"]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)n["description"]) || ((string)n["description"]).Length>160)return false;
            if(notes.Select(n=>(string)n["referenceId"]).Distinct().Count()!=notes.Count)return false;
            if(status=="supported" && !notes.Any(n=>(string)n["referenceId"]==selected))return false;
            result=r;reason=null;return true;
        } catch(Exception e) when(e is JsonException || e is InvalidCastException || e is ArgumentException) {return false;}
    }
}
