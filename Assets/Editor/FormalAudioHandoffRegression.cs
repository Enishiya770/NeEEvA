using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class FormalAudioHandoffRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private sealed class Saved { public object owner, value; public FieldInfo field; }
    private sealed class Context
    {
        public ChatSample chat; public AudioSource audio; public AudioClip filler, replacement;
        public GPTSoVITSFASTAPI provider; public FormalAudioEarlyFailureTTS early; public FormalAudioPausedStartTTS paused;
        public ChatSetting settings; public Coroutine player; public int checks, generation, round=990001;
        public bool started, done, success; public AudioClip issued;
        public readonly List<Saved> saved = new List<Saved>();
        public void Check(bool ok,string why){ checks++; if(!ok)throw new InvalidOperationException(why); }
        public void Set(object owner,string name,object value)
        {
            var field=Field(owner.GetType(),name);
            if(!saved.Exists(x=>x.owner==owner&&x.field==field))saved.Add(new Saved{owner=owner,field=field,value=field.GetValue(owner)});
            field.SetValue(owner,value);
        }
        public void Arm()
        {
            provider.CancelStreaming(); audio.Stop(); audio.clip=filler;
            generation++; Set(chat,"m_FormalResponseGeneration",generation);
            Set(chat,"m_LatencyFillerPlayed",true);Set(chat,"m_LatencyFillerFromEou",false);
            Set(chat,"m_LatencyFillerClip",filler); Set(chat,"m_LatencyFillerStartedAt",Time.realtimeSinceStartup-20f);
            Set(chat,"m_LatencyFillerDuration",.1f); Set(chat,"m_LatencyFillerText","test opener");
            started=done=success=false;issued=null;
        }
        public TTS.StreamingPlaybackPermission Permission() => (TTS.StreamingPlaybackPermission)Call(chat,"GetFormalPlaybackPermission",generation,round);
        public void Begin(Func<TTS.StreamingPlaybackPermission> gate,Action onStart=null)
        {
            provider.SpeakStreamingWithPlaybackGate(new SpeechText("Fixture speech.","en"),audio,
                _=>{ started=true;issued=audio.clip;onStart?.Invoke(); },
                (ok,_,duration)=>{ success=ok;done=true; },gate);
        }
        public void Restore()
        {
            if(player!=null)chat.StopCoroutine(player);provider.CancelStreaming();early.CancelStreaming();paused.CancelStreaming();audio.Stop();
            Call(chat,"StopDirectStreamingSubtitle");
            for(int i=saved.Count-1;i>=0;i--)saved[i].field.SetValue(saved[i].owner,saved[i].value);
        }
    }

    public static IEnumerator RunChecks(ChatSample chat,string output)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Use the isolated Unity Play fixture; never the user's running scene.");
        string directory=Path.Combine(output,"formal-audio-handoff");Directory.CreateDirectory(directory);
        LastReport=new JObject{["status"]="running",["cases"]=new JArray(),
            ["scope"]="Real GPTSoVITSFASTAPI HTTP/WAV/PCM playback against a local deterministic WAV fixture, plus actual production ChatSample playback coroutines. AudioSource is muted. No Qwen/real TTS service request, no human audibility claim."};
        var host=new GameObject("Formal audio handoff fixture");
        var c=new Context{chat=chat,audio=host.AddComponent<AudioSource>(),provider=host.AddComponent<GPTSoVITSFASTAPI>(),
            early=host.AddComponent<FormalAudioEarlyFailureTTS>(),paused=host.AddComponent<FormalAudioPausedStartTTS>(),settings=(ChatSetting)Get(chat,"m_ChatSettings"),
            generation=(int)Get(chat,"m_FormalResponseGeneration")+100};
        c.audio.playOnAwake=false;c.audio.mute=true;
        c.filler=AudioClip.Create("owned opener",48000,1,48000,false);
        c.replacement=AudioClip.Create("foreign replacement",48000*8,1,48000,false);
        var stack=new Stack<IEnumerator>();
        using(var server=new WavServer())
        try
        {
            c.Set(c.provider,"m_PostURL",server.Url); c.Set(chat,"m_AudioSource",c.audio);
            c.Set(chat,"m_AgentRunning",false);c.Set(chat,"m_FormalResponseInFlight",false);
            c.Set(chat,"m_SilencedSpeechRoundIds",new HashSet<int>());
            c.Set(chat,"m_LastTtsFailureNoticeGeneration",-1);
            // Keep artificial fixture onset measurements out of the real turn's
            // listening anchors and restore prediction/filler bookkeeping later.
            foreach(string field in new[]{"m_FirstAudioLatencyEstimateSec","m_EouFillerGeneration","m_EouFillerScheduled",
                "m_EouTurnWasSinging","m_EouSingingRejectedByFinal","m_EouCognitiveSpeechVeto","m_EouCognitiveSingingSupport",
                "m_FinalModeVerdict","m_FinalModeSoftDowngrade","m_PendingSingingConfirmation","m_EouFillerContext"})
                c.Set(chat,field,Get(chat,field));
            c.Set(chat,"m_ListeningFormalLatencyArmed",false);c.Set(chat,"m_ListeningLatencyArmed",false);
            c.Set(chat,"m_EouTime",0f);
            c.Set(chat,"m_FirstAudioProgressSource",null);c.Set(chat,"m_FirstAudioProgressClip",null);
            stack.Push(Cases(c));
            while(stack.Count>0)
            {
                object yielded=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var e=stack.Peek();if(!e.MoveNext()){(stack.Pop()as IDisposable)?.Dispose();continue;}
                        if(e.Current is IEnumerator nested){stack.Push(nested);continue;}
                        yielded=e.Current;break;
                    }
                }
                catch(Exception error){LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return yielded;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop()as IDisposable)?.Dispose();c.Restore();
            LastReport["checks"]=c.checks;LastReport["localHttpRequests"]=server.RequestCount;
            if((string)LastReport["status"]=="running")LastReport["status"]="failed";
            File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");
            UnityEngine.Object.DestroyImmediate(c.filler);UnityEngine.Object.DestroyImmediate(c.replacement);UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static IEnumerator Cases(Context c)
    {
        c.Arm();
        c.Set(c.chat,"m_ListeningLatencyArmed",true);c.Set(c.chat,"m_ListeningClosedAt",Time.realtimeSinceStartup);
        Call(c.chat,"ObserveVoiceOutputFirstAudio",false,false);
        c.Check((bool)Get(c.chat,"m_ListeningLatencyArmed"),"No source progress cannot consume the generic first-audio receipt.");
        c.audio.Play();Call(c.chat,"ObserveVoiceOutputFirstAudio",true,true);
        yield return Until(()=>c.audio.timeSamples>0,2,"generic first-audio source progress");
        Call(c.chat,"ObserveVoiceOutputFirstAudio",true,false);
        c.Check(!(bool)Get(c.chat,"m_ListeningLatencyArmed"),"Generic first-audio receipt is consumed once DSP playback advances.");
        c.Set(c.chat,"m_ListeningLatencyArmed",true);
        Call(c.chat,"ObserveVoiceOutputFirstAudio",true,false);
        c.Check((bool)Get(c.chat,"m_ListeningLatencyArmed"),"Already playing old audio cannot consume a subsequently armed listening receipt.");
        c.Set(c.chat,"m_ListeningLatencyArmed",false);
        Add("generic first-audio progress and old-playback isolation");

        c.Arm(); c.audio.Play(); c.Set(c.chat,"m_LatencyFillerStartedAt",Time.realtimeSinceStartup);
        c.Set(c.chat,"m_LatencyFillerDuration",c.filler.length);
        c.Check(c.Permission()==TTS.StreamingPlaybackPermission.Wait,"Actual owned opener waits.");
        yield return Until(()=>!c.audio.isPlaying,3,"natural opener completion");
        c.Check(c.Permission()==TTS.StreamingPlaybackPermission.Play,"Naturally drained opener permits handoff while bookkeeping remains.");
        Add("actual opener natural drain");

        c.Arm();
        c.Begin(()=> (TTS.StreamingPlaybackPermission)StaticCall("ResolveStreamingHandoff",true,
            (bool)Get(c.chat,"m_LatencyFillerPlayed")&&c.audio.isPlaying,20f,.1f));
        yield return Until(()=>c.done,5,"legacy gate same-frame failure");
        c.Check(c.started&&!c.success,"Legacy any-isPlaying gate reproduces first PCM self-cancellation.");
        Add("reproduced legacy race through real provider");

        c.Arm();c.Begin(c.Permission);
        yield return Until(()=>c.started,5,"fixed PCM start");
        c.Check(!c.done,"Fixed gate survives same-frame IsCurrent after assigning the formal clip.");
        c.Check(c.Permission()==TTS.StreamingPlaybackPermission.Play,"New formal clip cannot be mistaken for the old filler.");
        bool advanced=false;
        yield return (IEnumerator)Call(c.chat,"AwaitFormalAudioAdvancement",c.generation,c.round,c.issued,
            (Func<bool>)(()=>c.done),(Action<bool>)(ok=>advanced=ok));
        c.Check(advanced&&c.audio.timeSamples>0,"Real PCM advances before successful onset evidence.");
        yield return Until(()=>c.done,5,"PCM natural drain");
        c.Check(c.success,"Fixed real provider completes natural drain successfully.");
        Add("fixed natural handoff and DSP progress");

        c.Arm();c.Begin(c.Permission,()=>c.Set(c.chat,"m_FormalResponseGeneration",c.generation+1));
        yield return Until(()=>c.done,5,"new generation cancellation");
        c.Check(c.started&&!c.success,"New generation still cancels a just-issued old clip.");
        Add("new-generation revocation");

        c.Arm();c.Begin(c.Permission,()=>{c.audio.clip=c.replacement;c.audio.Play();});
        yield return Until(()=>c.done,5,"foreign replacement cancellation");
        c.Check(!c.success&&c.audio.clip==c.replacement&&c.audio.isPlaying,"Old provider cleanup cannot stop a replacement clip.");
        Add("foreign clip ownership after callback replacement");

        c.Arm();c.Begin(c.Permission,()=>{c.audio.clip=c.replacement;c.audio.Play();c.provider.CancelStreaming();});
        yield return Until(()=>c.done,5,"explicit cancel with replacement");
        c.Check(!c.success&&c.audio.clip==c.replacement&&c.audio.isPlaying,"CancelStreaming respects the exact clip owner too.");
        Add("explicit CancelStreaming preserves replacement");

        c.Arm(); c.audio.clip=c.replacement;c.audio.Play();
        c.Set(c.chat,"m_LatencyFillerFromEou",true);
        c.Set(c.chat,"<IsAISpeaking>k__BackingField",true);
        c.Set(c.chat,"m_CurrentlyPlayingText","replacement utterance");
        Call(c.chat,"CancelPendingEouLatencyFiller","fixture",true);
        c.Check(c.audio.clip==c.replacement&&c.audio.isPlaying,"Canceling stale EOU filler does not stop unrelated audio.");
        c.Check(c.chat.IsAISpeaking&&(string)Get(c.chat,"m_CurrentlyPlayingText")=="replacement utterance",
            "Stale filler cleanup cannot clear the new utterance's speaking and presentation state.");
        Add("stale EOU filler cancellation");

        c.Arm();PreparePlayer(c,c.early);c.early.Done=false;
        c.player=c.chat.StartCoroutine((IEnumerator)Call(c.chat,"StreamDirectTTSPlayer",c.generation));
        yield return Until(()=>c.early.Done,2,"same-frame failing provider callback");
        yield return null;yield return null;
        c.Check(!(bool)Get(c.chat,"m_RealFirstAudioStarted"),"Play callback plus immediate failure does not record formal first audio.");
        c.Check(((HashSet<string>)Get(c.chat,"m_ActuallySpokenRounds")).Count==0,"Unadvanced clip does not count as an actually spoken round.");
        c.chat.StopCoroutine(c.player);c.player=null;
        Add("production direct player rejects immediate failed onset");

        c.Arm();PreparePlayer(c,c.paused);
        c.player=c.chat.StartCoroutine((IEnumerator)Call(c.chat,"StreamDirectTTSPlayer",c.generation));
        yield return null;
        c.Check(c.paused.Paused&&!c.paused.Cancelled&&!(bool)Get(c.chat,"m_RealFirstAudioStarted"),
            "Pending first-sample rebuffer neither cancels playback nor records onset.");
        yield return Until(()=>(bool)Get(c.chat,"m_RealFirstAudioStarted"),3,"paused first-sample provider resumes");
        c.Check(!c.paused.Cancelled&&c.audio.timeSamples>0,"The same paused provider resumes and produces verified first audio.");
        c.chat.StopCoroutine(c.player);c.player=null;c.paused.CancelStreaming();
        Add("production direct player permits first-sample rebuffer pause");

        c.Arm();PreparePlayer(c,c.provider);
        c.player=c.chat.StartCoroutine((IEnumerator)Call(c.chat,"StreamDirectTTSPlayer",c.generation));
        yield return Until(()=>(bool)Get(c.chat,"m_RealFirstAudioStarted"),5,"production first audio after progress");
        c.Check(c.audio.isPlaying&&c.audio.timeSamples>0,"Production first-audio timestamp requires real source progress.");
        c.Check(((HashSet<string>)Get(c.chat,"m_ActuallySpokenRounds")).Count==1,"Only advanced formal audio registers a spoken round.");
        c.chat.StopCoroutine(c.player);c.player=null;c.provider.CancelStreaming();
        Add("production direct player accepts actual PCM advancement");
    }

    private static void PreparePlayer(Context c,TTS provider)
    {
        // Spoken-round accounting is an agent feature. Pause autonomous ticks
        // separately, so the production counter runs without a model request.
        c.Set(c.chat,"m_AgentRunning",true);c.Set(c.chat,"m_SceneTimePaused",true);
        c.Set(c.settings,"m_TextToSpeech",provider);c.Set(c.chat,"m_StreamComplete",false);
        c.Set(c.chat,"m_RealFirstAudioStarted",false);c.Set(c.chat,"m_TTSSenderDone",false);
        c.Set(c.chat,"<IsAISpeaking>k__BackingField",false);c.Set(c.chat,"m_AgentCurrentRoundIsTick",true);
        c.Set(c.chat,"m_ActuallySpokenRounds",new HashSet<string>());
        c.Set(c.chat,"m_AssistantHeardText",new StringBuilder());
        c.Set(c.chat,"m_ConsecutiveAITurns",0);
        var type=Get(c.chat,"m_PendingChunks").GetType();var queue=Activator.CreateInstance(type);
        c.Set(c.chat,"m_PendingChunks",queue);
        var ct=typeof(ChatSample).GetNestedType("PendingSpeechChunk",BindingFlags.NonPublic);
        object chunk=Activator.CreateInstance(ct,true);
        ct.GetField("RoundId").SetValue(chunk,c.round);ct.GetField("Text").SetValue(chunk,"Fixture speech.");
        ct.GetField("LanguageCode").SetValue(chunk,"en");
        type.GetMethod("Enqueue").Invoke(queue,new[]{chunk});
    }
    private static void Add(string s)=>((JArray)LastReport["cases"]).Add(s);
    private static IEnumerator Until(Func<bool> ok,float limit,string message)
    {float end=Time.realtimeSinceStartup+limit;while(!ok()){if(Time.realtimeSinceStartup>end)throw new TimeoutException(message);yield return null;}}
    private static FieldInfo Field(Type t,string n)
    {for(;t!=null;t=t.BaseType){var f=t.GetField(n,Flags);if(f!=null)return f;}throw new MissingFieldException(n);}
    private static object Get(object o,string n)=>Field(o.GetType(),n).GetValue(o);
    private static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,Flags).Invoke(o,a);
    private static object StaticCall(string n,params object[] a)=>typeof(ChatSample).GetMethod(n,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,a);

    private sealed class WavServer:IDisposable
    {
        private readonly TcpListener listener;private volatile bool stopped;private int count;
        public int RequestCount=>System.Threading.Volatile.Read(ref count);
        public string Url{get;}
        public WavServer()
        {listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/tts";Task.Run((Action)Serve);}
        private void Serve()
        {
            byte[] wav=MakeWav();
            while(!stopped)
            try
            {
                using(var client=listener.AcceptTcpClient())using(var stream=client.GetStream())
                {
                    client.ReceiveTimeout=5000;client.SendTimeout=5000;var head=new List<byte>();
                    while(head.Count<65536)
                    {int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();head.Add((byte)b);int n=head.Count;if(n>=4&&head[n-4]==13&&head[n-3]==10&&head[n-2]==13&&head[n-1]==10)break;}
                    int length=0;foreach(string line in Encoding.ASCII.GetString(head.ToArray()).Split('\n'))
                        if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Substring(15).Trim());
                    if(length<0||length>1000000)throw new InvalidDataException();
                    var body=new byte[length];int read=0;while(read<length){int n=stream.Read(body,read,length-read);if(n<=0)throw new EndOfStreamException();read+=n;}
                    System.Threading.Interlocked.Increment(ref count);
                    byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: "+wav.Length+"\r\nConnection: close\r\n\r\n");
                    stream.Write(header,0,header.Length);stream.Write(wav,0,wav.Length);
                }
            }
            catch(Exception){if(stopped)break;}
        }
        private static byte[] MakeWav()
        {
            const int rate=24000,samples=24000;
            using(var bytes=new MemoryStream())using(var w=new BinaryWriter(bytes))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+samples*2);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(rate);w.Write(rate*2);w.Write((short)2);w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));w.Write(samples*2);
                for(int i=0;i<samples;i++)w.Write((short)(Math.Sin(2*Math.PI*220*i/rate)*1200));
                return bytes.ToArray();
            }
        }
        public void Dispose(){stopped=true;listener.Stop();}
    }
}

public sealed class FormalAudioEarlyFailureTTS:TTS
{
    public bool Done;public override bool SupportsStreamingPlayback=>true;
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech,AudioSource output,Action<string> onStarted,
        Action<bool,string,float> onCompleted,Func<StreamingPlaybackPermission> gate)
    {
        var clip=AudioClip.Create("same-frame canceled PCM",48000,1,48000,false);
        output.clip=clip;output.Play();onStarted?.Invoke(speech.Text);
        output.Stop();output.clip=null;UnityEngine.Object.Destroy(clip);onCompleted?.Invoke(false,speech.Text,1);Done=true;
    }
}

public sealed class FormalAudioPausedStartTTS:TTS
{
    public bool Paused,Cancelled;private Coroutine routine;private AudioSource output;private AudioClip clip;
    public override bool SupportsStreamingPlayback=>true;
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech,AudioSource target,Action<string> onStarted,
        Action<bool,string,float> onCompleted,Func<StreamingPlaybackPermission> gate)
    {
        CancelStreaming();Cancelled=false;Paused=false;output=target;
        clip=AudioClip.Create("pending first PCM rebuffer",48000,1,48000,false);
        output.clip=clip;output.Play();output.Pause();Paused=true;onStarted?.Invoke(speech.Text);
        routine=StartCoroutine(Resume(speech,onCompleted));
    }
    private IEnumerator Resume(SpeechText speech,Action<bool,string,float> done)
    {
        yield return new WaitForSecondsRealtime(.15f);
        if(Cancelled||output==null||output.clip!=clip)yield break;
        output.UnPause();Paused=false;
        while(output!=null&&output.clip==clip&&output.isPlaying)yield return null;
        done?.Invoke(!Cancelled,speech.Text,1);
    }
    public override void CancelStreaming()
    {
        Cancelled=true;if(routine!=null)StopCoroutine(routine);routine=null;
        if(output!=null&&output.clip==clip){output.Stop();output.clip=null;}
        if(clip!=null)UnityEngine.Object.Destroy(clip);clip=null;
    }
}
