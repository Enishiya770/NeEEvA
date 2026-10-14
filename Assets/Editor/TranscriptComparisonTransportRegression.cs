using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class TranscriptComparisonTransportRegression
{
    private static async Task Serve(TcpListener listener,string body,int delay)
    {
        using(var client=await listener.AcceptTcpClientAsync()) using(var stream=client.GetStream()) {
            stream.ReadTimeout=5000;var header=new StringBuilder();int b;
            while((b=stream.ReadByte())>=0) {header.Append((char)b);if(header.ToString().EndsWith("\r\n\r\n"))break;}
            int count=0;foreach(string line in header.ToString().Split('\n'))
                if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))int.TryParse(line.Substring(15).Trim(),out count);
            for(int i=0;i<count;i++)if(stream.ReadByte()<0)break;
            await Task.Delay(delay);
            byte[] data=Encoding.UTF8.GetBytes(body), response=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+data.Length+"\r\nConnection: close\r\n\r\n");
            try {await stream.WriteAsync(response,0,response.Length);await stream.WriteAsync(data,0,data.Length);}
            catch(IOException) { } // Deadline/owner revocation closes the client.
        }
    }
    public static IEnumerator Run(ChatQW model,string output)
    {
        FieldInfo url=null;
        for(Type t=typeof(ChatQW);t!=null&&url==null;t=t.BaseType)url=t.GetField("url",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
        string original=(string)url.GetValue(model);var rows=new JArray();int history=model.m_DataList.Count;
        var packet=new JObject { ["capture"]=42,["stream"]=new JObject{["text"]="停止播放。"},["full"]=new JObject{["text"]="继续播放。"} };
        foreach(string name in new[]{"already-superseded","malformed","wrong-binding","deadline","owner-revoked"}) {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            url.SetValue(model,"http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1/chat/completions");
            string response=new JObject { ["choices"]=new JArray(new JObject { ["finish_reason"]="stop",["message"]=new JObject {
                ["content"]="{\"streamText\":\"wrong\",\"fullText\":\"wrong\",\"comparison\":\"same\",\"relation\":\"equivalent\"}" } }) }.ToString();
            Task server=name=="already-superseded"?null:Serve(listener,name=="malformed"?"{bad-json":response,name=="deadline"?3400:name=="owner-revoked"?300:0);
            bool called=false,equivalent=false;float start=Time.realtimeSinceStartup;
            try {
                yield return model.ReviewTranscriptComparison(packet,()=>name!="already-superseded" && (name!="owner-revoked"||Time.realtimeSinceStartup-start<.05f),value=>{called=true;equivalent=value;});
                float elapsed=Time.realtimeSinceStartup-start;
                bool cancelled=name=="already-superseded"||name=="owner-revoked";
                bool passed=!equivalent&&called!=cancelled&&model.m_DataList.Count==history&&elapsed<4f&&
                    (name!="already-superseded"||!listener.Pending());
                rows.Add(new JObject{["name"]=name,["passed"]=passed,["callback"]=called,["equivalent"]=equivalent,["seconds"]=elapsed});
                File.WriteAllText(Path.Combine(output,"transcript-transport.json"),rows.ToString());
                if(!passed)throw new InvalidOperationException("Transcript comparison transport failed: "+name);
                if(cancelled)listener.Stop(); // cancellation may precede TCP accept
                if(server!=null) {
                    float until=Time.realtimeSinceStartup+6f;
                    while(!server.IsCompleted&&Time.realtimeSinceStartup<until)yield return null;
                    if(!server.IsCompleted)throw new TimeoutException("Loopback fixture did not finish after owner revocation.");
                    if(server.IsFaulted && !(cancelled && server.Exception.Flatten().InnerExceptions.All(e=>e is IOException || e is SocketException || e is ObjectDisposedException)))throw server.Exception;
                }
            } finally {listener.Stop();url.SetValue(model,original);}
        }
    }
}
