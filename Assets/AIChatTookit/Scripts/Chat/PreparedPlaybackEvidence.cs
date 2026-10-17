using System;
using Newtonsoft.Json.Linq;

// One completion receipt for the local WAV consumer and its review handoff.
// Wall time alone is never evidence that audio advanced.
public sealed class PreparedPlaybackEvidence
{
    readonly int total,frequency;
    readonly double startedDsp,startedReal,pitch;
    int lastSample;
    double lastDsp,lastReal;
    bool advanced;
    string revoked;
    public PreparedPlaybackEvidence(int samples,int rate,double dsp,double real,double speed)
    { total=samples;frequency=rate;startedDsp=lastDsp=dsp;startedReal=lastReal=real;pitch=speed; }
    public void Revoke(string reason) { if(revoked==null)revoked=reason??"explicit-stop"; }
    public void Observe(int sample,double dsp,double real,double speed)
    {
        if(sample<lastSample)Revoke("sample-position-rewound");
        if(Math.Abs(speed-pitch)>.001)Revoke("pitch-changed");
        advanced|=sample>0;lastSample=Math.Max(lastSample,sample);lastDsp=dsp;lastReal=real;
    }
    public JObject Finish(double dsp,double real,bool ownerCurrent,bool paused)
    {
        double gap=Math.Max(0,dsp-lastDsp),realGap=Math.Max(0,real-lastReal);
        double remaining=frequency>0&&pitch>0?(total-lastSample)/(frequency*pitch):double.PositiveInfinity;
        double duration=frequency>0&&pitch>0?total/(frequency*pitch):double.PositiveInfinity;
        // Samples reset to zero on natural drain. Account for the DSP interval
        // since the last observed advancement, with a bounded missing-frame gap.
        bool reached=advanced && gap<=2.5 && realGap<=2.5 &&
            remaining<=gap+.02 && dsp-startedDsp>=duration-.02;
        string reason=revoked??(!ownerCurrent?"owner-or-clip-changed":paused?"paused":
            !advanced?"no-observed-advancement":gap>2.5||realGap>2.5?"observation-gap":
            !reached?"stopped-before-observed-drain":"observed-dsp-drain");
        bool complete=revoked==null&&ownerCurrent&&!paused&&reached;
        return new JObject{["complete"]=complete,["reason"]=reason,["ownerCurrent"]=ownerCurrent,
            ["paused"]=paused,["lastPlayingSample"]=lastSample,["totalSamples"]=total,["frequency"]=frequency,
            ["pitch"]=pitch,["dspGap"]=gap,["realtimeGap"]=realGap,["remainingAudioSeconds"]=remaining,
            ["elapsedDsp"]=dsp-startedDsp,["elapsedRealtime"]=real-startedReal,["reachedEnd"]=reached,
            ["scope"]="Observed advancement plus bounded DSP drain, excluding owned stop/pause/replacement; not physical audibility."};
    }
}
