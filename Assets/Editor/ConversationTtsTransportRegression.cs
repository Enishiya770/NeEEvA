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

// Exercise the real Unity PCM transport and muted AudioSource. Synthetic loopback
// responses isolate HTTP success, a truncated 200, malformed PCM and cancellation.
public static class ConversationTtsTransportRegression
{
    public static JArray LastReport { get; private set; }
    private static byte[] Wav(bool odd, int bytes=32000)
    {
        using(var s=new MemoryStream()) using(var w=new BinaryWriter(s)) {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(32000);w.Write(64000);w.Write((short)2);w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));w.Write(0);
            for(int i=0;i<bytes/2;i++)w.Write((short)(Math.Sin(i*.1)*1500));
            if(odd)w.Write((byte)1);return s.ToArray();
        }
    }
    private static async Task Serve(TcpListener listener, byte[] bytes, bool truncated, bool starve=false, bool slow=false, bool badRequest=false)
    {
        using(var client=await listener.AcceptTcpClientAsync()) using(var stream=client.GetStream()) {
            stream.ReadTimeout=5000;var header=new StringBuilder();int b;
            while((b=stream.ReadByte())>=0) {header.Append((char)b);if(header.ToString().EndsWith("\r\n\r\n"))break;}
            int count=0;foreach(string line in header.ToString().Split('\n'))
                if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))int.TryParse(line.Substring(15).Trim(),out count);
            for(int i=0;i<count;i++)if(stream.ReadByte()<0)break;
            byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+(badRequest?"400 Bad Request\r\nContent-Type: application/json":"200 OK\r\nContent-Type: audio/wav")+"\r\nX-Request-ID: tts-loopback\r\nContent-Length: "+
                (bytes.Length+(truncated?64000:0))+"\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response,0,response.Length);
            if(slow)await Task.Delay(250);
            if(starve) {
                await stream.WriteAsync(bytes,0,64044);await stream.FlushAsync();await Task.Delay(1500);
                await stream.WriteAsync(bytes,64044,bytes.Length-64044);
            } else await stream.WriteAsync(bytes,0,bytes.Length);
        }
    }
    public static IEnumerator Run(string output)
    {
        LastReport=new JArray();
        foreach(string name in new[]{"complete","truncated-200","odd-pcm","cancel-playback","gap-rebuffer",
            "prepared-complete","prepared-truncated-200","prepared-odd-pcm","prepared-cancel","prepared-http400","stream-http400"}) {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            bool prepared=name.StartsWith("prepared-");
            bool badRequest=name.EndsWith("http400");
            var service=Serve(listener,badRequest?Encoding.UTF8.GetBytes("{\"detail\":\"unsupported-language-test\"}"):Wav(name.EndsWith("odd-pcm"),name=="gap-rebuffer"?128000:32000),name.EndsWith("truncated-200"),name=="gap-rebuffer",name=="prepared-cancel",badRequest);
            var host=new GameObject("Muted TTS transport "+name);var source=host.AddComponent<AudioSource>();
            source.volume=0;source.mute=true;host.AddComponent<AudioListener>();
            var tts=host.AddComponent<GPTSoVITSFASTAPI>();
            typeof(TTS).GetField("m_PostURL",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(tts,"http://127.0.0.1:"+port+"/tts");
            bool done=false,success=false,started=false;float began=Time.realtimeSinceStartup;JObject receipt=null;
            Application.LogCallback log=(text,stack,kind)=> {
                string prefix=prepared?"[TTS/PreparedReceipt] ":"[TTS/StreamReceipt] ";if(text.StartsWith(prefix))receipt=JObject.Parse(text.Substring(prefix.Length));
            };
            Application.logMessageReceived+=log;
            try {
                AudioClip preparedClip=null;
                if(prepared) {
                    tts.PrepareSpeech(new SpeechText("確認しました。","ja"),(clip,words)=>{preparedClip=clip;done=true;success=clip!=null;});
                    if(name=="prepared-cancel") {
                        while(Time.realtimeSinceStartup-began<.1f)yield return null;
                        tts.CancelPreparedSpeech();
                    }
                } else tts.SpeakStreamingWithPlaybackGate(new SpeechText("確認しました。","ja"),source,_=>started=true,
                    (ok,text,duration)=>{done=true;success=ok;},()=>name=="cancel-playback"&&started ? TTS.StreamingPlaybackPermission.Cancel:TTS.StreamingPlaybackPermission.Play);
                while(!done&&Time.realtimeSinceStartup-began<12)yield return null;
                bool expected=name=="complete"||name=="gap-rebuffer"||name=="prepared-complete";
                bool passed=done&&success==expected&&receipt!=null&&(bool)receipt["completePlaybackConfirmed"]==(!prepared&&expected)&&
                    (name!="truncated-200"||((int)receipt["httpStatus"]==200&&(bool)receipt["synthesisSucceeded"]==false))&&
                    (name!="cancel-playback"||(string)receipt["cancellation"]=="playback-gate")&&
                    (name!="odd-pcm"||(bool)receipt["HasOddByte"])&&
                    (name!="gap-rebuffer"||(int)receipt["streamUnderruns"]>0)&&
                    (name!="prepared-cancel"||(string)receipt["cancellation"]=="superseded")&&
                    (!badRequest||((int)receipt["httpStatus"]==400&&((string)receipt["errorBody"]??"").Contains("unsupported-language-test")&&(string)receipt["serverRequestId"]=="tts-loopback"));
                if(preparedClip!=null) {tts.ObservePreparedSpeech(preparedClip,"discarded");UnityEngine.Object.Destroy(preparedClip);}
                LastReport.Add(new JObject { ["name"]=name,["passed"]=passed,["completed"]=done,["success"]=success,["receipt"]=receipt });
                File.WriteAllText(Path.Combine(output,"tts-transport.json"),LastReport.ToString());
                if(!passed)throw new InvalidOperationException("TTS transport regression failed: "+name);
            } finally { Application.logMessageReceived-=log;tts.CancelStreaming();listener.Stop();UnityEngine.Object.Destroy(host); }
            while(!service.IsCompleted)yield return null;
            if(service.IsFaulted && !(name=="prepared-cancel" && service.Exception.Flatten().InnerExceptions.All(e=>e is IOException || e is SocketException)))throw service.Exception;
            yield return null;
        }
    }
}
