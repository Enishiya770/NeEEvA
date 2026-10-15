using System;
using System.Collections;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_ActivityMonitorGeneration;
    private UnityWebRequest m_ActivityMonitorRequest;
    public override bool SupportsActivityMonitor => true;
    public override void CancelActivityMonitor()
    {
        ++m_ActivityMonitorGeneration;
        if(m_ActivityMonitorRequest!=null) {
            try {m_ActivityMonitorRequest.Abort();} catch(Exception) { }
            m_ActivityMonitorRequest=null;
        }
    }
    public override void PostActivityMonitor(string input,Action<bool,string,string> callback)
    {
        CancelActivityMonitor();
        StartCoroutine(RequestActivityMonitor(input,m_ActivityMonitorGeneration,callback));
    }
    private string BuildActivityMonitorRequest(string input)
    {
        if(string.IsNullOrEmpty(input)||input.Length>ActivityMonitorProtocol.MaximumInputCharacters)return null;
        string schema;
        try{schema=ActivityMonitorProtocol.SchemaForFrame(JObject.Parse(input));}catch(Exception){return null;}
        var sb=new StringBuilder("{\"model\":");AppendJsonString(sb,CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":640,\"response_format\":{\"type\":\"json_object\"");
        if(m_Backend==BackendType.Local)sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb,new SendData("system",ActivityMonitorProtocol.Contract+(m_Backend==BackendType.Local?"":schema)));
        sb.Append(',');AppendMessage(sb,new SendData("user",input));sb.Append(']');
        if(m_Backend==BackendType.Local)sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        // Do not pin this concurrent, short request to the main planner's slot.
        sb.Append('}');return sb.ToString();
    }
    private IEnumerator RequestActivityMonitor(string input,int generation,Action<bool,string,string> callback)
    {
        string json=BuildActivityMonitorRequest(input);
        var budget=json==null?null:MeasureActivityMonitorBudget(json);
        if(json==null || (bool?)budget?["allowed"]!=true) {
            Debug.LogWarning("[Activity/MonitorBudget] "+(budget?.ToString(Formatting.None)??"input-cap"));
            callback?.Invoke(false,"","activity-monitor-budget");yield break;
        }
        float start=Time.realtimeSinceStartup;
        using(var request=new UnityWebRequest(url,"POST")) {
            m_ActivityMonitorRequest=request;
            request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler=new DownloadHandlerBuffer();request.timeout=8;
            request.SetRequestHeader("Content-Type","application/json");
            request.SetRequestHeader("Authorization","Bearer "+(string.IsNullOrEmpty(api_key)?"ollama":api_key));
            request.SetRequestHeader("X-Request-ID",Guid.NewGuid().ToString("N"));
            yield return request.SendWebRequest();
            if(ReferenceEquals(m_ActivityMonitorRequest,request))m_ActivityMonitorRequest=null;
            if(generation!=m_ActivityMonitorGeneration)yield break;
            string output="",status="activity-monitor-transport";
            bool ok=request.result==UnityWebRequest.Result.Success && request.responseCode==200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text,out output,out status);
            Debug.Log("[Activity/MonitorWire] "+new JObject{["generation"]=generation,["ok"]=ok,["status"]=status,
                ["elapsedSeconds"]=Time.realtimeSinceStartup-start,["requestCharacters"]=json.Length,["budget"]=budget,["blocksExecution"]=false}.ToString(Formatting.None));
            callback?.Invoke(ok,output,status);
        }
    }

    // This concurrent request has its own small complete-payload budget.
    // Existing planner/reply ceilings remain unchanged.
    private static JObject MeasureActivityMonitorBudget(string json)
    {
        var wire=JObject.Parse(json);
        long textUnits=((JArray)wire["messages"]).OfType<JObject>()
            .Sum(message=>EstimateRoomDecisionTextUnits(RoomTaskMessageText(message)));
        long schemaUnits=EstimateRoomDecisionTextUnits(wire.SelectToken("response_format.schema")?.ToString(Formatting.None)??"");
        int completion=(int)wire["max_tokens"],bytes=Encoding.UTF8.GetByteCount(json);
        long total=textUnits+schemaUnits+completion+2048;
        return new JObject{["estimatedTextUnits"]=textUnits,["schemaReserve"]=schemaUnits,["completionReserve"]=completion,
            ["framingReserve"]=2048,["estimatedTotalUnits"]=total,["textCeiling"]=6144,["totalCeiling"]=8192,
            ["wireBytes"]=bytes,["allowed"]=textUnits<=6144&&total<=8192&&bytes<=131072&&completion>0&&completion<=640,
            ["scope"]="Complete monitor request estimate, including bounded reasoning/output; not tokenizer usage. No change to main planner/reply budgets."};
    }
}
