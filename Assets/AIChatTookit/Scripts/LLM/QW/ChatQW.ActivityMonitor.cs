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
    private string m_ActivityMonitorCorrelation;
    private Action<bool,string,string> m_ActivityMonitorCompletion;
    public override bool SupportsActivityMonitor => true;
    public override string PrepareActivityMonitorFrame(string input,out string diagnostics)
    {
        var source=ActivityEvidencePolicy.Parse(input);var attempts=new JArray();
        for(int pressure=0;pressure<=2;pressure++) {
            var frame=(string)source["purpose"]=="speech-meaning"?source:ActivityMonitorContextPolicy.Project(source,pressure);
            string value=frame.ToString(Formatting.None),wire=BuildActivityMonitorRequest(value);
            var budget=wire==null?null:MeasureActivityMonitorBudget(wire);
            attempts.Add(new JObject{["pressure"]=pressure,["sizes"]=ActivityMonitorContextPolicy.Sizes(frame),["budget"]=budget});
            if((bool?)budget?["allowed"]==true) {
                diagnostics=new JObject{["admitted"]=true,["attempts"]=attempts}.ToString(Formatting.None);return value;
            }
        }
        diagnostics=new JObject{["admitted"]=false,["attempts"]=attempts}.ToString(Formatting.None);return null;
    }
    public override void CancelActivityMonitor()
        => CancelActivityMonitorForReason("owner-cancel");
    public override void CancelActivityMonitorForReason(string reason)
    {
        ++m_ActivityMonitorGeneration;
        var completion=m_ActivityMonitorCompletion;
        m_ActivityMonitorCompletion=null;
        if(m_ActivityMonitorRequest!=null) {
            Debug.Log("[Activity/MonitorCancel] "+new JObject{["requestId"]=m_ActivityMonitorCorrelation,
                ["generation"]=m_ActivityMonitorGeneration-1,["reason"]=reason??"owner-cancel",["abortRequested"]=true}.ToString(Formatting.None));
            try {m_ActivityMonitorRequest.Abort();} catch(Exception) { }
            m_ActivityMonitorRequest=null;
        }
        completion?.Invoke(false,"","activity-monitor-cancelled");
    }
    public override void PostActivityMonitor(string input,Action<bool,string,string> callback)
    {
        CancelActivityMonitorForReason("superseded-monitor-request");
        m_ActivityMonitorCompletion=callback;
        StartCoroutine(RequestActivityMonitor(input,m_ActivityMonitorGeneration,callback));
    }
    private string BuildActivityMonitorRequest(string input)
    {
        if(string.IsNullOrEmpty(input)||input.Length>ActivityMonitorProtocol.MaximumInputCharacters)return null;
        string schema;
        try{schema=ActivityMonitorProtocol.CompactSchema(ActivityMonitorProtocol.SchemaForFrame(JObject.Parse(input)));}catch(Exception){return null;}
        var sb=new StringBuilder("{\"model\":");AppendJsonString(sb,CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":640,\"response_format\":{\"type\":\"json_object\"");
        // Share identical native field definitions without changing any branch,
        // evidence ID or validation rule. Admission measures this final wire.
        if(m_Backend==BackendType.Local)sb.Append(",\"schema\":").Append(RoomTaskProtocol.CompactNativeDecisionSchema(schema));
        sb.Append("},\"messages\":[");
        var delivery=JObject.Parse(input)["delivery"] as JObject;
        bool noSpeech=(JObject.Parse(input)["speech"] as JArray)?.Count==0;
        bool hasAction=(JObject.Parse(input)["actions"] as JArray)?.Count>0;
        bool deliveryOnly=delivery!=null && noSpeech;
        // Concurrent monitor packets repeat small frozen bodies and bindings.
        // Exact round-trip verification and net wire saving still gate sharing.
        var wireFrame=JObject.Parse(input);
        if((string)wireFrame["purpose"]=="speech-meaning") {
            AppendMessage(sb,new SendData("system",ActivitySpeechMeaningPolicy.Contract+(m_Backend==BackendType.Local?"":schema)));
            sb.Append(',');AppendMessage(sb,new SendData("user",input));sb.Append(']');
            if(m_Backend==BackendType.Local)sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
            sb.Append('}');return sb.ToString();
        }
        ResultPlaybackSelection.ProjectWireAudio(wireFrame["delivery"] as JObject);
        string compactInput=SceneFactContextCompactor.CompactJson(wireFrame.ToString(Formatting.None),out var sharing,128,inlineEvidence:true);
        AppendMessage(sb,new SendData("system",(deliveryOnly?ActivityDeliveryProtocol.Contract:noSpeech?ActivityMonitorProtocol.ActionContract:
            (int?)wireFrame["evidenceVersion"]>=2?ActivityMonitorProtocol.SpeechContractForFrame(wireFrame):ActivityMonitorProtocol.Contract)+(delivery!=null?ResultPlaybackSelection.Contract:"")+
            (hasAction && !noSpeech || hasAction && deliveryOnly ? "\n"+ActivityMonitorProtocol.ActionContract:"")+
            (delivery!=null?(deliveryOnly?"":ActivityDeliveryProtocol.Contract)+"\n本次补句的实际语言码是 "+(string)delivery["language"]+"，遵守此语言，不跟随较早对话的语言。":"")+
            (!noSpeech && (int?)JObject.Parse(input)["evidenceVersion"]>=2?"\n纠正台词用当前 speech.language；comparison 的中文只用于诊断，不得复制为台词。":"")+
            (sharing.applied?"\n"+SceneFactContextCompactor.JsonSharingContract:"")+(m_Backend==BackendType.Local?"":schema)));
        sb.Append(',');AppendMessage(sb,new SendData("user",compactInput));sb.Append(']');
        if(m_Backend==BackendType.Local)sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        // Do not pin this concurrent, short request to the main planner's slot.
        sb.Append('}');return sb.ToString();
    }
    public override void PostActivityVisualEvidence(string input,string imageDataUrl,Action<bool,string,string> callback)
    {
        CancelActivityMonitorForReason("same-source-visual-recheck");m_ActivityMonitorCompletion=callback;
        StartCoroutine(RequestActivityMonitor(input,m_ActivityMonitorGeneration,callback,imageDataUrl));
    }
    private string BuildActivityVisualRequest(string input,string imageDataUrl)
    {
        if(string.IsNullOrEmpty(imageDataUrl)||input.Length>3000)return null;
        var packet=JObject.Parse(input);bool grouped=packet["claims"] is JArray;
        string schema=grouped?ActivityVisualEvidenceProtocol.GroupSchema(packet).ToString(Formatting.None):ActivityVisualEvidenceProtocol.Schema;
        string contract=grouped?ActivityVisualEvidenceProtocol.GroupContract:ActivityVisualEvidenceProtocol.Contract;
        var sb=new StringBuilder("{\"model\":");AppendJsonString(sb,CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":320,\"response_format\":{\"type\":\"json_object\"");
        if(m_Backend==BackendType.Local)sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");AppendMessage(sb,new SendData("system",contract+(m_Backend==BackendType.Local?"":schema)));
        sb.Append(',');AppendMessage(sb,new SendData("user",input){imageDataUrl=imageDataUrl});sb.Append(']');
        if(m_Backend==BackendType.Local)sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        sb.Append('}');return sb.ToString();
    }
    private IEnumerator RequestActivityMonitor(string input,int generation,Action<bool,string,string> callback,string imageDataUrl=null)
    {
        string json=imageDataUrl==null?BuildActivityMonitorRequest(input):BuildActivityVisualRequest(input,imageDataUrl);
        string correlation=(string)JObject.Parse(input)["monitorRequestId"]??Guid.NewGuid().ToString("N");
        var budget=json==null?null:MeasureActivityMonitorBudget(json);
        if(json==null || (bool?)budget?["allowed"]!=true) {
            Debug.LogWarning("[Activity/MonitorBudget] "+(budget?.ToString(Formatting.None)??"input-cap"));
            m_ActivityMonitorCompletion=null;
            callback?.Invoke(false,"","activity-monitor-budget");yield break;
        }
        float start=Time.realtimeSinceStartup;
        using(var request=new UnityWebRequest(url,"POST")) {
            m_ActivityMonitorRequest=request;
            m_ActivityMonitorCorrelation=correlation;
            request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler=new DownloadHandlerBuffer();request.timeout=8;
            request.SetRequestHeader("Content-Type","application/json");
            request.SetRequestHeader("Authorization","Bearer "+(string.IsNullOrEmpty(api_key)?"ollama":api_key));
            request.SetRequestHeader("X-Request-ID",correlation);
            yield return request.SendWebRequest();
            if(ReferenceEquals(m_ActivityMonitorRequest,request))m_ActivityMonitorRequest=null;
            string output="",status="activity-monitor-transport";
            bool ok=request.result==UnityWebRequest.Result.Success && request.responseCode==200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text,out output,out status);
            string errorBody=ok?null:request.downloadHandler?.text;
            if(errorBody?.Length>2048)errorBody=errorBody.Substring(0,2048);
            Debug.Log("[Activity/MonitorWire] "+new JObject{["generation"]=generation,["requestId"]=correlation,["ok"]=ok,["status"]=status,
                ["httpStatus"]=request.responseCode,["transportError"]=request.error,["rawVerdict"]=output,
                ["errorBody"]=errorBody,
                ["elapsedSeconds"]=Time.realtimeSinceStartup-start,["requestCharacters"]=json.Length,["budget"]=budget,["blocksExecution"]=false,
                ["ownerCurrent"]=generation==m_ActivityMonitorGeneration,["serverRequestId"]=request.GetResponseHeader("X-Request-ID"),
                ["terminalResult"]=request.result.ToString(),["responseBytes"]=request.downloadedBytes}.ToString(Formatting.None));
            if(generation!=m_ActivityMonitorGeneration)yield break;
            m_ActivityMonitorCompletion=null;
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
        int images=wire.SelectTokens("messages[*].content[*].image_url").Count();
        long imageReserve=images*4096;
        long total=textUnits+schemaUnits+completion+2048+imageReserve;
        return new JObject{["estimatedTextUnits"]=textUnits,["schemaReserve"]=schemaUnits,["completionReserve"]=completion,
            ["framingReserve"]=2048,["estimatedTotalUnits"]=total,["textCeiling"]=6144,["totalCeiling"]=8192,
            ["imageCount"]=images,["imageReserve"]=imageReserve,
            ["wireBytes"]=bytes,["allowed"]=images<=1&&textUnits<=6144&&total<=8192&&bytes<=131072&&completion>0&&completion<=640,
            ["scope"]="Complete monitor request estimate, including bounded reasoning/output; not tokenizer usage. No change to main planner/reply budgets."};
    }
}
