using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_SceneRecallGeneration;
    private UnityWebRequest m_SceneRecallRequest;
    public override bool SupportsSceneRecallMessages => true;
    public override void CancelSceneRecallMessage()
    {
        ++m_SceneRecallGeneration;
        var old=m_SceneRecallRequest;m_SceneRecallRequest=null;
        if(old!=null)try{if(!old.isDone)old.Abort();}catch(Exception){}
    }
    public override void PostSceneRecallMessage(string input,string image,Action<bool,string,string> callback)
    {
        CancelSceneRecallMessage();
        if(string.IsNullOrEmpty(image)||!image.StartsWith("data:image/",StringComparison.Ordinal))
        {callback?.Invoke(false,"","recall-image-unavailable");return;}
        var facts=JObject.Parse(input);
        var schema=SceneRecallProtocol.Schema(facts).ToString(Formatting.None);
        var sb=new StringBuilder("{\"model\":");AppendJsonString(sb,CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":768,\"response_format\":{\"type\":\"json_object\"");
        if(m_Backend==BackendType.Local)sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb,new SendData("system",SceneRecallProtocol.SourceContract(facts)+SceneRecallProtocol.Contract+"\n"+schema));sb.Append(',');
        AppendMessage(sb,new SendData("user",input){imageDataUrl=image});sb.Append(']');
        if(m_Backend==BackendType.Local)sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb,k_SlotAuxiliary);sb.Append('}');
        StartCoroutine(RequestSceneRecall(sb.ToString(),facts,m_SceneRecallGeneration,callback));
    }
    private IEnumerator RequestSceneRecall(string payload,JObject facts,int generation,Action<bool,string,string> callback)
    {
        bool allowed=AdmitSceneSearchWire(payload,out var budget);
        budget["generation"]=generation;budget["captureId"]=facts["captureId"];
        Debug.Log("[SceneRecall/Budget] "+budget.ToString(Formatting.None));
        if(!allowed){callback?.Invoke(false,"","recall-context-budget");yield break;}
        float started=Time.realtimeSinceStartup;
        using(var request=new UnityWebRequest(url,"POST")) {
            m_SceneRecallRequest=request;
            request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler=new DownloadHandlerBuffer();request.timeout=10;
            request.SetRequestHeader("Content-Type","application/json");
            request.SetRequestHeader("Authorization","Bearer "+(string.IsNullOrEmpty(api_key)?"ollama":api_key));
            yield return request.SendWebRequest();
            if(ReferenceEquals(m_SceneRecallRequest,request))m_SceneRecallRequest=null;
            if(generation!=m_SceneRecallGeneration)yield break;
            string output="",status=request.error??"recall-http-failure";
            bool ok=request.result==UnityWebRequest.Result.Success&&request.responseCode==200&&
                TryReadRoomTaskCompletion(request.downloadHandler.text,out output,out status)&&SceneRecallProtocol.TryRead(output,facts,out _,out status);
            Debug.Log("[SceneRecall/Wire] "+new JObject{["generation"]=generation,["captureId"]=facts["captureId"],
                ["ok"]=ok,["status"]=status,["seconds"]=Time.realtimeSinceStartup-started,["output"]=output}.ToString(Formatting.None));
            callback?.Invoke(ok,output,ok?"received":status);
        }
    }
}
