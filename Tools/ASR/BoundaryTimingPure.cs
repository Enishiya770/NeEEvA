using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace UnityEngine
{
    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a,b);
        public static float Min(float a, float b) => Math.Min(a,b);
        public static float Lerp(float a, float b, float t) => a+(b-a)*Math.Clamp(t,0f,1f);
    }
    public static class Debug { public static void Log(object ignored) { } }
}

public static class BoundaryTimingPure
{
    private static readonly List<string> checks = new List<string>();
    private const BindingFlags PrivateStatic = BindingFlags.Static|BindingFlags.NonPublic;
    private static bool Admission(bool covered, float remaining, float observed, float age, out string reason)
    {
        object[] args={covered,remaining,observed,age,null};
        bool result=(bool)typeof(RecorderBudget).GetMethod("ShouldStartRemoteBoundaryReview",PrivateStatic).Invoke(null,args);
        reason=(string)args[4]; return result;
    }
    private static void Admit(bool covered,float remaining,float observed,float age,bool expected,string expectedReason)
    {
        bool result=Admission(covered,remaining,observed,age,out string reason);
        Check(result==expected && reason==expectedReason,expectedReason+" remaining="+remaining+" observed="+observed+" age="+age);
    }
    private static void Sample(RecorderBudget b,float now,bool completed)
    {
        b.m_BoundaryRequestTimingRecorded=false;
        typeof(RecorderBudget).GetMethod("ObserveBoundaryRequestDuration",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(b,new object[]{now,completed});
    }
    private static string Key(string value) => (string)typeof(RecorderBudget).GetMethod("NormalizeSemanticTranscript",PrivateStatic).Invoke(null,new object[]{value});
    private static bool NewSound(float reviewed,float latest) => (bool)typeof(RecorderBudget).GetMethod("HasSoundAdvanced",PrivateStatic).Invoke(null,new object[]{reviewed,latest});
    private static void Text(TurnActivityEvidence a,string text,int ms,float time) => a.ObserveAsr(Key(text),"",ms,time,true,0,0,0,.005f,true,0,-1,time);
    private static bool Near(float a,float b) => Math.Abs(a-b)<.00001f;
    private static void Check(bool ok,string name) { if(!ok) throw new Exception(name); checks.Add(name); }

    public static int Main(string[] args)
    {
        bool passed=false; string error=null;
        try
        {
            Func<bool[],bool> prepared = flags => (bool)typeof(RecorderBudget)
                .GetMethod("CanUsePreparedOrdinaryEndpoint", PrivateStatic)
                .Invoke(null, Array.ConvertAll(flags, x => (object)x));
            Check(prepared(new[]{true,true,false,true,false,true}), "Complete current final preview plus fresh quiet supports configured short endpoint");
            Check(!prepared(new[]{false,true,false,true,false,true}), "Unconfirmed noise candidate does not get accelerated endpoint");
            Check(!prepared(new[]{true,false,false,true,false,true}), "Partial text or pending final is not completed analysis");
            Check(!prepared(new[]{true,true,true,true,false,true}), "Singing or current melody retains its longer endpoint");
            Check(!prepared(new[]{true,true,false,false,false,true}), "Stale or uncovered audio cannot take short endpoint");
            Check(!prepared(new[]{true,true,false,true,true,true}), "Resumed speech or pending transcript prevents short endpoint");
            Check(!prepared(new[]{true,true,false,true,false,false}), "A completed preview cannot cover a newer acoustic revision");
            Admit(true,.8f,0,0,true,"initial-measurement");
            Admit(true,.8f,2.5f,5,false,"slower-than-remaining-quiet");
            Admit(true,.8f,.2f,5,true,"fits-quiet-budget");
            Admit(true,.29f,.2f,5,false,"slower-than-remaining-quiet");
            Admit(true,0,0,0,false,"capture-close-due");
            Admit(true,-.1f,7,31,false,"capture-close-due");
            Admit(true,.8f,2.5f,31,true,"latency-refresh-probe");
            Admit(false,-.1f,7,0,true,"not-covered-quiet");
            int attempts=0; float observed=0;
            for(int i=0;i<4;i++) if(Admission(true,.8f,observed,i*5,out _)) { attempts++; observed=.8f; }
            Check(attempts==1,"Synthetic four covered quiet turns reduce remote starts from four to one after a cancellation lower bound");
            var b=new RecorderBudget {m_StalledReviewRemoteAllowed=false,m_StalledTranscriptReviewRequested=true,m_StalledTranscriptReviewRequestTime=10};
            Sample(b,12,true); Check(b.m_ObservedBoundarySeconds==0,"Prepared-only latency is ignored");
            b.m_StalledReviewRemoteAllowed=true;
            Sample(b,12f,true); Check(b.m_ObservedBoundarySeconds==0,"Even slow synchronous preparation cannot train remote latency");
            b.m_BoundaryRemoteTimingArmed=true;
            Sample(b,10.8f,false); Check(Near(b.m_ObservedBoundarySeconds,.8f),"Cancelled latency is retained as lower bound");
            typeof(RecorderBudget).GetMethod("ObserveBoundaryRequestDuration",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(b,new object[]{13f,false});
            Check(Near(b.m_ObservedBoundarySeconds,.8f),"A request contributes at most one latency sample");
            Sample(b,10.4f,false); Check(Near(b.m_ObservedBoundarySeconds,.8f),"Shorter cancellation cannot lower an estimate");
            Sample(b,10.4f,true); Check(Near(b.m_ObservedBoundarySeconds,.7f),"Completed refresh gradually lowers estimate");
            Sample(b,20,false); Check(Near(b.m_ObservedBoundarySeconds,7),"Lost callback timeout is bounded at seven seconds");
            var a=new TurnActivityEvidence(); a.Reset(10);
            Text(a,"先说",1000,10.1f);
            Check(a.Revision==1 && a.ActivityRevision==0,"New words do not invent physical speech activity");
            Text(a,"先 说。",1300,10.4f);
            Check(a.Revision==1 && !a.PendingText,"Punctuation or whitespace-only partial changes do not advance semantic revision");
            Text(a,"别说",1600,10.7f);
            Check(a.PendingText && a.Revision==1,"A one-character correction remains pending until corroborated");
            Text(a,"别说",1900,11);
            Check(!a.PendingText && a.Revision==2 && a.ActivityRevision==0,"Corroborated semantic correction advances text revision only");
            Text(a,"先说",2200,11.3f); Text(a,"先说",2500,11.6f);
            Check(a.Revision-a.ActivityRevision==3,"Away-and-back transcript has a different text version despite the same spelling");
            Check(Key("5身边")!=Key("我身边"),"No heuristic replacement of 5 with 我 is introduced");
            a.Reset(10);
            for(int i=0;i<20;i++) a.ObserveLevel(.05f,10+i*.06f,.01f,.003f);
            int onset=a.ActivityRevision; float sound=a.LatestSoundAt;
            for(int i=20;i<30;i++) a.ObserveLevel(.05f,10+i*.06f,.01f,.003f);
            Check(a.ActivityRevision==onset,"Continuous vowel does not need a new onset version");
            Check(NewSound(sound,a.LatestSoundAt),"New sound guard still rejects a decision during a continuing vowel");
            Check(!NewSound(-1,11) && !NewSound(10,10.05f),"Missing baseline or insignificant timestamp jitter does not invent continuation");
            passed=true;
        }
        catch(Exception e) { error=e.ToString(); }
        File.WriteAllText(args[0],JsonSerializer.Serialize(new {passed,checks,error,scope="Exact production helper extraction; offline synthetic evidence; no real session ASR accuracy or latency claim"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine((passed?"PASS":"FAIL")+" "+checks.Count+" checks"+(error==null?"":"\n"+error));
        return passed?0:1;
    }
}
