#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Isolated-only, read-only tap on one actual AudioSource's DSP output.
/// Begin/End run on the main thread. The audio callback copies into preallocated
/// bounded memory; it never modifies data, allocates, writes files or calls Unity.</summary>
public sealed class FirstAudioReferenceCapture : MonoBehaviour
{
    private const int MaximumChannels = 8;
    private const int MaximumCallbacks = 16384;
    private const int MaximumSeconds = 20;
    private readonly object gate = new object();
    private Session session;
    public JObject LastMetadata { get; private set; }

    private struct Block
    {
        public int SampleStart, Frames;
        public long StopwatchTicks, UtcTicks;
    }
    private sealed class Session
    {
        public string Directory;
        public float[] Samples;
        public Block[] Blocks;
        public int SampleRate, Channels, Count, BlockCount, Active, EndReason;
        public long BeginStopwatch, BeginUtcTicks, FirstStopwatch, FirstUtcTicks;
        public double BeginDspTime;
    }

    public bool IsCapturing => session != null && Volatile.Read(ref session.Active) != 0;

    public void Begin(string directory)
    {
        if(!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Reference capture is isolated Play-only.");
        if(GetComponent<AudioSource>()==null)throw new InvalidOperationException("Attach to the actual AudioSource host.");
        if(session!=null)throw new InvalidOperationException("End the prior capture before starting another.");
        int rate=AudioSettings.outputSampleRate;
        if(rate<8000||rate>192000)throw new InvalidOperationException("Unsupported audio output sample rate.");
        string target=Path.GetFullPath(directory);System.IO.Directory.CreateDirectory(target);
        // All allocations occur before the tap becomes active.
        var next=new Session{Directory=target,SampleRate=rate,
            Samples=new float[rate*MaximumSeconds*MaximumChannels],Blocks=new Block[MaximumCallbacks],
            BeginStopwatch=Stopwatch.GetTimestamp(),BeginUtcTicks=DateTime.UtcNow.Ticks,BeginDspTime=AudioSettings.dspTime,Active=1};
        lock(gate)session=next;
        LastMetadata=null;
    }

    private void OnAudioFilterRead(float[] data,int channels)
    {
        // Stopwatch and DateTime are managed clock reads, not Unity audio APIs.
        long ticks=Stopwatch.GetTimestamp(),utc=DateTime.UtcNow.Ticks;
        lock(gate)
        {
            var s=session;
            if(s==null||s.Active==0)return;
            if(ticks-s.BeginStopwatch>=MaximumSeconds*Stopwatch.Frequency){s.EndReason=1;s.Active=0;return;}
            if(channels<1||channels>MaximumChannels||(s.Channels!=0&&channels!=s.Channels))
            {s.EndReason=2;s.Active=0;return;}
            if(s.BlockCount>=s.Blocks.Length){s.EndReason=3;s.Active=0;return;}
            s.Channels=channels;
            int maximum=s.SampleRate*MaximumSeconds*channels;
            int take=Math.Min(data.Length,maximum-s.Count);
            take-=take%channels;
            if(take<=0){s.EndReason=4;s.Active=0;return;}
            if(s.BlockCount==0){s.FirstStopwatch=ticks;s.FirstUtcTicks=utc;}
            s.Blocks[s.BlockCount++]=new Block{SampleStart=s.Count,Frames=take/channels,StopwatchTicks=ticks,UtcTicks=utc};
            Array.Copy(data,0,s.Samples,s.Count,take);
            s.Count+=take;
            if(s.Count>=maximum){s.EndReason=4;s.Active=0;}
        }
    }

    public JObject End()
    {
        Session ended;
        lock(gate)
        {
            ended=session;if(ended==null)return LastMetadata;
            ended.Active=0;session=null;
        }
        // No audio-thread lock is held while encoding/writing the result.
        string pcm=Path.Combine(ended.Directory,"reference.f32");
        using(var output=new BinaryWriter(File.Create(pcm)))
            for(int i=0;i<ended.Count;i++)output.Write(ended.Samples[i]);
        var blocks=new JArray();
        for(int i=0;i<ended.BlockCount;i++)
        {
            var b=ended.Blocks[i];
            blocks.Add(new JObject{["sampleStart"]=b.SampleStart,["frameStart"]=b.SampleStart/Math.Max(1,ended.Channels),
                ["frames"]=b.Frames,["stopwatchTicks"]=b.StopwatchTicks,
                ["utc"]=new DateTime(b.UtcTicks,DateTimeKind.Utc).ToString("O")});
        }
        LastMetadata=new JObject{["status"]=ended.Count>0?"captured":"no-audio-callback",
            ["scope"]="Unmodified samples received by OnAudioFilterRead on this AudioSource host. This is before physical output and not proof of audibility.",
            ["format"]="little-endian float32 interleaved",["pcmPath"]=pcm,["sampleRate"]=ended.SampleRate,
            ["channels"]=ended.Channels,["sampleCount"]=ended.Count,["frames"]=ended.Count/Math.Max(1,ended.Channels),
            ["maximumSeconds"]=MaximumSeconds,["endReason"]=ended.EndReason==0?"explicit-end":ended.EndReason==1?"maximum-wall-duration":ended.EndReason==2?"unsupported-or-changed-channels":ended.EndReason==3?"callback-bound": "maximum-samples",
            ["stopwatchFrequency"]=Stopwatch.Frequency,["beginStopwatchTicks"]=ended.BeginStopwatch,
            ["beginUtc"]=new DateTime(ended.BeginUtcTicks,DateTimeKind.Utc).ToString("O"),["beginDspTime"]=ended.BeginDspTime,
            ["firstCallbackStopwatchTicks"]=ended.FirstStopwatch,
            ["firstCallbackUtc"]=ended.BlockCount==0?JValue.CreateNull():new JValue(new DateTime(ended.FirstUtcTicks,DateTimeKind.Utc).ToString("O")),
            ["blocks"]=blocks,
            ["timingLimitations"]="Audio callback timestamps describe DSP processing, not physical output. Callback gaps must be checked before treating concatenated PCM as continuous. Match this waveform against independently captured system loopback."};
        File.WriteAllText(Path.Combine(ended.Directory,"reference.json"),LastMetadata.ToString()+"\n");
        return LastMetadata;
    }
    public static int RunSyntheticChecks(string directory)
    {
        var host=new GameObject("Read-only audio reference synthetic checks");
        host.AddComponent<AudioSource>().enabled=false;
        var tap=host.AddComponent<FirstAudioReferenceCapture>();
        try
        {
            var source=new[]{.1f,-.2f,.3f,-.4f};var original=(float[])source.Clone();
            tap.Begin(Path.Combine(directory,"reference-copy"));tap.OnAudioFilterRead(source,2);var result=tap.End();
            for(int i=0;i<source.Length;i++)if(source[i]!=original[i])throw new InvalidOperationException("Audio tap modified source samples.");
            using(var reader=new BinaryReader(File.OpenRead((string)result["pcmPath"])))
                foreach(float value in original)if(reader.ReadSingle()!=value)throw new InvalidOperationException("Reference copy differs from source.");
            if((int)result["sampleCount"]!=4||(int)result["channels"]!=2)throw new InvalidOperationException("Reference shape changed.");
            tap.Begin(Path.Combine(directory,"reference-wall-bound"));tap.session.BeginStopwatch-=21*Stopwatch.Frequency;
            tap.OnAudioFilterRead(source,2);result=tap.End();
            if((int)result["sampleCount"]!=0||(string)result["endReason"]!="maximum-wall-duration")throw new InvalidOperationException("Reference exceeded its wall-clock bound.");
            return 4;
        }
        finally{tap.End();UnityEngine.Object.DestroyImmediate(host);}
    }
    private void OnDisable(){End();}
}

#endif
