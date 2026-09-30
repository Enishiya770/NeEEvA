using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;

public static class ObservabilityRegression
{
    private static int checks;
    private static void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
    private static byte[] Wav(int samples, int rate = 1000, short value = 400)
    {
        using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
        {
            w.Write(0x46464952); w.Write(36 + samples * 2); w.Write(0x45564157);
            w.Write(0x20746d66); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
            w.Write((short)2); w.Write((short)16); w.Write(0x61746164); w.Write(samples * 2);
            for (int i=0;i<samples;i++) w.Write(value);
            return s.ToArray();
        }
    }
    public static void Main()
    {
        SemanticTraceRegression.Main();
        string raw="<thought>心理活动保留。<thought>嵌套</thought>仍在心里</thought><lang code=\"ja\"/><speech mode=\"independent\"/><say>こんにちは。</say><look/>";
        var baseline=RoleOutputChannels.Parse(raw,RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        for (int i=0;i<=raw.Length;i++)
        {
            var events=new List<string>(); var speech=new List<SpeechText>();
            var parser=new RoleOutputChannels(speech.Add,RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions,events.Add);
            parser.Push(raw.Substring(0,i));parser.Push(raw.Substring(i));parser.Finish();
            Check(parser.Speech==baseline.Speech && parser.PrivateCharacters==baseline.PrivateCharacters && parser.ToExecutableText()==baseline.ToExecutableText(),"observer preserves channels at every split");
            Check(events.SequenceEqual(new[]{"role.thought_opened","role.thought_closed","role.action_candidate"}),"correct nested thought boundaries");
            var throwing=new RoleOutputChannels(null,RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions,_=>throw new Exception());
            throwing.Push(raw.Substring(0,i));throwing.Push(raw.Substring(i));throwing.Finish();
            Check(throwing.ToExecutableText()==baseline.ToExecutableText(),"broken diagnostic sink cannot change behavior");
        }
        var logs=new List<string>();var trace=new ConversationTrace("capture",logs.Add);var fields=new JObject{["captureSerial"]=4};
        Check(!trace.TryGetFirstFormalAudioLatencySeconds(out _), "No latency before an input closes and audio advances");
        trace.Mark("capture.started",fields);fields["captureSerial"]=9;
        trace.Sample("asr.partial_sample");trace.Sample("asr.partial_sample");trace.Mark("eot.capture_closed");
        Check(logs.Count==4 && (int)JObject.Parse(logs[0])["fields"]["captureSerial"]==4,"immutable fields and repeated samples");
        Check((double)JObject.Parse(logs[3])["elapsedMs"] >= (double)JObject.Parse(logs[0])["elapsedMs"],"capture precedes EOT; EOT need not be zero");
        Check(!trace.TryGetFirstFormalAudioLatencySeconds(out _), "Closed recording alone is not a first audio measurement");
        var continuation = trace.Child("recovery");
        continuation.Mark("audio.public_filler_started");
        continuation.Child("formal-delivery").Mark("audio.formal_playback_advanced");
        var formalEvent = JObject.Parse(logs.Last());
        Check(continuation.TryGetFirstFormalAudioLatencySeconds(out double firstSeconds) &&
            Math.Abs(firstSeconds * 1000 - (double)formalEvent["fields"]["captureCloseToFirstFormalAudioMs"]) < .001,
            "Human-readable timing uses exactly the same frozen capture-to-formal anchor as trace");
        Check((bool)formalEvent["fields"]["firstFormalForTurn"] &&
            Math.Abs((double)formalEvent["fields"]["captureCloseToFirstFormalAudioMs"] -
                ((double)formalEvent["elapsedMs"] - (double)JObject.Parse(logs[3])["elapsedMs"])) < .001,
            "Recovery first audio is measured from original capture close, independently of opening audio");
        var interruptLogs = new List<string>();
        var rejectedInterrupt = new ConversationTrace("new-input-candidate", interruptLogs.Add);
        rejectedInterrupt.Mark("eot.capture_closed");
        continuation.Child("later-subpipeline").Mark("eot.capture_closed");
        continuation.Child("later-playback").Mark("audio.formal_playback_advanced");
        var repeated = JObject.Parse(logs.Last());
        Check(continuation.TryGetFirstFormalAudioLatencySeconds(out double laterSeconds) && firstSeconds == laterSeconds &&
            !rejectedInterrupt.TryGetFirstFormalAudioLatencySeconds(out _),
            "New weak capture and duplicate completion cannot move a recovered reply's printed first audio latency");
        Check(!(bool)repeated["fields"]["firstFormalForTurn"] &&
            JToken.DeepEquals(formalEvent["fields"]["captureCloseToFirstFormalAudioMs"], repeated["fields"]["captureCloseToFirstFormalAudioMs"]),
            "Rejected input or later continuation never rewrites original first-audio timing");
        var textLogs = new List<string>();
        var typed = new ConversationTrace("text", textLogs.Add);
        typed.Mark("audio.formal_playback_advanced");
        Check(!typed.TryGetFirstFormalAudioLatencySeconds(out _), "Typed playback cannot inherit a microphone's first audio latency");
        Check(JObject.Parse(textLogs[0])["fields"]["captureCloseToFirstFormalAudioMs"].Type == JTokenType.Null,
            "A typed turn without capture must not inherit a previous audio latency anchor");
        byte[] source=Wav(10000);byte[] before=(byte[])source.Clone();
        WaveformTrace.RegisterInput(source,42);
        Check((bool)WaveformTrace.Describe((byte[])source.Clone())["mappingComplete"],"byte-identical clone carries provenance");
        var trimmed=Wav(3000,value:401); WaveformTrace.Transform(source,trimmed,2,5);
        WaveformTrace.Annotate(trimmed,"clip:synthetic",3);
        var r=(JObject)WaveformTrace.Describe(trimmed)["regions"][0];
        Check((double)r["sourceStartSeconds"]==2 && (double)r["sourceEndSeconds"]==5 && (int)r["revision"]==3,"actual trim and material identity");
        var annotatedClone=WaveformTrace.Describe((byte[])trimmed.Clone());
        Check((bool)annotatedClone["mappingComplete"] && (double)annotatedClone["regions"][0]["sourceStartSeconds"]==2 && annotatedClone["regions"][0]["revision"]==null,"annotation does not destroy clone timing or invent material identity");
        var paced=Wav(6000,value:402); WaveformTrace.Transform(trimmed,paced,0,3);
        var chunk=Wav(2000,value:403); WaveformTrace.Transform(paced,chunk,2,4);
        r=(JObject)WaveformTrace.Describe(chunk)["regions"][0];
        Check((double)r["sourceStartSeconds"]==3 && (double)r["sourceEndSeconds"]==4,"pace then chunk maps source interval");
        var joined=Wav(8100,value:404);WaveformTrace.Concatenate(joined,new[]{paced,chunk},new[]{0f,.1f});
        var j=WaveformTrace.Describe(joined);
        Check((bool)j["mappingComplete"] && ((JArray)j["regions"]).Count==3 && (string)j["regions"][1]["kind"]=="inserted-silence","composition preserves gap and regions");
        var spanning=Wav(2200,value:405);WaveformTrace.Transform(joined,spanning,5.9,8.1);
        Check(((JArray)WaveformTrace.Describe(spanning)["regions"]).Count==3,"chunk across composition gap");
        Check(source.SequenceEqual(before),"diagnostics never mutate samples");
        Check(!(bool)WaveformTrace.Describe(Wav(1337,value:407))["mappingComplete"],"unknown waveform stays unknown");
        var secondRecording=(byte[])source.Clone();WaveformTrace.RegisterInput(secondRecording,43);
        Check(!(bool)WaveformTrace.Describe((byte[])source.Clone())["mappingComplete"],"ambiguous identical recording cannot invent provenance");
        Check((int)WaveformTrace.Describe(source)["regions"][0]["captureSerial"]==42 && (int)WaveformTrace.Describe(secondRecording)["regions"][0]["captureSerial"]==43,"identity lookup remains isolated");
        Check(WaveformTrace.Describe(new byte[2])["durationSeconds"].Type==JTokenType.Null,"bad WAV duration unknown");
        Console.WriteLine("PASS: "+checks+" additional observability/unchanged-behavior/PCM-lineage checks");
    }
}
