using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private readonly Dictionary<string,JObject> m_WorkPlaybackReadings=new Dictionary<string,JObject>();
    private int m_WorkPlaybackMeaningRequests;
    private const string PlaybackReadingContract="只解释 text 已播原话，不判断现实任务是否完成，也不补足 context 中的要求。输入不提供执行回执或期望答案。先说明原话实际提及的动作和对象，再解释其时间/阶段；context仅用于省略和指代，不能把它要求的其他动作添加到释义中。stage=prospective(未来打算/承诺)、preparing(尚在准备)、ongoing(正在进行)、terminal-outcome(原话明确完成/失败/停止，或报告检查所得的未找到、无法辨认等结果)、unknown-outcome(原话明确说该操作是否完成/结果如何无法确认)、non-action(主观表达、邀请或无法消解)、mixed(同句多个动作处于不同阶段，meaning逐个说明)。正在执行不等于已经完成；一个动作完成不能扩展为另一个动作完成。meaning用简短完整释义，不迎合任务、不猜测未说的内容。JSON readings逐项照抄id。";

    private IEnumerator PrepareWorkPlaybackMeanings(string json,JObject receipt,int generation,float firstStartedAt,
        WorkReviewTrace trace,Action<string,JArray,string> done)
    {
        var inputs=WorkResultDeliveryProtocol.MeaningInputs(receipt);var readings=new JArray();
        string CacheKey(JObject row)=>CurrentModelName+"|"+url+"|"+(string)row["id"];
        var unique=inputs.OfType<JObject>().GroupBy(r=>(string)r["id"]).Select(g=>g.First()).ToArray();
        var missing=unique.Where(r=>!m_WorkPlaybackReadings.ContainsKey(CacheKey(r))).ToArray();
        for(int offset=0;offset<missing.Length;offset+=4) {
            if(generation!=m_EphemeralGeneration)yield break;
            float remaining=20f-(Time.realtimeSinceStartup-firstStartedAt);
            if(remaining<.5f){done(null,null,"work-playback-meaning-deadline");yield break;}
            var batch=new JArray(missing.Skip(offset).Take(4).Select(r=>r.DeepClone()));
            var rowSchema=new JObject{["type"]="object",["additionalProperties"]=false,["required"]=new JArray("id","meaning","stage"),
                ["properties"]=new JObject{["id"]=new JObject{["type"]="string",["enum"]=new JArray(batch.Select(r=>r["id"].DeepClone()))},
                    ["meaning"]=new JObject{["type"]="string",["minLength"]=1,["maxLength"]=96},
                    ["stage"]=new JObject{["type"]="string",["enum"]=new JArray("prospective","preparing","ongoing","terminal-outcome","unknown-outcome","non-action","mixed")}}};
            var schema=new JObject{["type"]="object",["additionalProperties"]=false,["required"]=new JArray("readings"),["properties"]=new JObject{
                ["readings"]=new JObject{["type"]="array",["minItems"]=batch.Count,["maxItems"]=batch.Count,["items"]=rowSchema}}};
            var wire=new JObject{["model"]=CurrentModelName,["stream"]=false,["temperature"]=0,["enable_thinking"]=false,["max_tokens"]=640,
                ["messages"]=new JArray(new JObject{["role"]="system",["content"]=PlaybackReadingContract+(m_Backend==BackendType.Local?"":schema.ToString(Formatting.None))},
                    new JObject{["role"]="user",["content"]=new JArray(batch.Select(r=>new JObject{
                        ["id"]=r["id"],["text"]=r["text"],["context"]=r["context"]})).ToString(Formatting.None)}),["response_format"]=new JObject{["type"]="json_object"}};
            if(m_Backend==BackendType.Local){wire["response_format"]["schema"]=schema;wire["chat_template_kwargs"]=new JObject{["enable_thinking"]=false};}
            string body=wire.ToString(Formatting.None);var budget=MeasureActivityMonitorBudget(body);
            string correlation=trace.Id+"-words-"+offset;
            if((bool?)budget["allowed"]!=true){LogReading("budget-rejected",correlation,batch,null,budget);done(null,null,"work-playback-meaning-budget");yield break;}
            using(var request=new UnityWebRequest(url,"POST")) {
                m_EphemeralRequest=request;trace.Request=request;
                try {
                    request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));request.downloadHandler=new DownloadHandlerBuffer();
                    request.timeout=Mathf.Max(1,Mathf.CeilToInt(Mathf.Min(8,remaining)));
                    request.SetRequestHeader("Content-Type","application/json");request.SetRequestHeader("Authorization","Bearer "+(string.IsNullOrEmpty(api_key)?"ollama":api_key));
                    request.SetRequestHeader("X-Request-ID",correlation);trace.Sent=true;
                    ++m_WorkPlaybackMeaningRequests;LogReading("sent",correlation,batch,null,budget);yield return request.SendWebRequest();
                    string raw="",error=null;
                    bool ok=request.responseCode==200&&request.result==UnityWebRequest.Result.Success&&TryReadRoomTaskCompletion(request.downloadHandler.text,out raw,out error);
                    budget["httpStatus"]=request.responseCode;budget["transportResult"]=request.result.ToString();budget["transportError"]=request.error;
                    if(!ok)budget["errorBody"]=(request.downloadHandler.text??"").Substring(0,Math.Min(2048,(request.downloadHandler.text??"").Length));
                    if(generation!=m_EphemeralGeneration){LogReading("cancelled-or-stale",correlation,batch,raw,budget);yield break;}
                    JArray parsed=null;try {parsed=JObject.Parse(raw)["readings"] as JArray;}catch(JsonException){}
                    ok=ok&&parsed!=null&&parsed.Count==batch.Count&&parsed.OfType<JObject>().Count()==batch.Count&&
                        parsed.All(v=>((JObject)v).Count==3&&v["id"]?.Type==JTokenType.String&&v["stage"]?.Type==JTokenType.String&&v["meaning"]?.Type==JTokenType.String)&&
                        batch.All(r=>parsed.Count(v=>(string)v["id"]==(string)r["id"])==1)&&
                        parsed.All(v=>v["meaning"]?.Type==JTokenType.String&&((string)v["meaning"]).Length>0&&((string)v["meaning"]).Length<=96&&
                            new[]{"prospective","preparing","ongoing","terminal-outcome","unknown-outcome","non-action","mixed"}.Contains((string)v["stage"]));
                    LogReading(ok?"received":"invalid-or-transport-failure",correlation,batch,raw,budget);
                    if(!ok){done(null,null,"work-playback-meaning-unchecked");yield break;}
                    foreach(var row in batch.OfType<JObject>()) {
                        var meaning=parsed.Single(v=>(string)v["id"]==(string)row["id"]);var complete=(JObject)row.DeepClone();
                        complete["meaning"]=meaning["meaning"].DeepClone();complete["stage"]=meaning["stage"].DeepClone();
                        if(m_WorkPlaybackReadings.Count>=96)m_WorkPlaybackReadings.Remove(m_WorkPlaybackReadings.Keys.First());
                        m_WorkPlaybackReadings[CacheKey(row)]=(JObject)complete.DeepClone();
                    }
                } finally {
                    if(ReferenceEquals(m_EphemeralRequest,request))m_EphemeralRequest=null;
                    if(ReferenceEquals(trace.Request,request))trace.Request=null;
                    if(generation!=m_EphemeralGeneration)LogReading("owner-ended",correlation,batch,null,budget);
                }
            }
        }
        foreach(var row in inputs.OfType<JObject>()) {
            if(!m_WorkPlaybackReadings.TryGetValue(CacheKey(row),out var cached)){done(null,null,"work-playback-meaning-cache-cap");yield break;}
            var boundReading=(JObject)row.DeepClone();boundReading["meaning"]=cached["meaning"].DeepClone();boundReading["stage"]=cached["stage"].DeepClone();readings.Add(boundReading);
        }
        var final=JObject.Parse(json);
        if(readings.Count>0 && final.SelectToken("response_format.schema") is JObject bound)
            WorkResultDeliveryProtocol.BindCoverageSchema(bound,(JArray)receipt["results"],readings);
        LogReading("bound",trace.Id,inputs,readings.ToString(Formatting.None),new JObject{["reused"]=unique.Length-missing.Length,["new"]=missing.Length});
        done(final.ToString(Formatting.None),readings,null);
    }

    private void LogReading(string stage,string id,JArray input,string raw,JObject budget)
    {
        Debug.Log("[Agent/WorkPlaybackMeaning] "+new JObject{["stage"]=stage,["requestId"]=id,["currentGeneration"]=m_EphemeralGeneration,
            ["input"]=input,["raw"]=raw,["budget"]=budget,["scope"]="Completed words only. No execution truth, action authority or delivery settlement. Owner/version-bound cache; full result comparison follows."}.ToString(Formatting.None));
    }
}
