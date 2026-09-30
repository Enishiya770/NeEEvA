using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

static class Program
{
    static int checks;
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static AudioClip Clip(int frames = 160, int channels = 1, int rate = 16000)
    { var c = AudioClip.Create("test", frames, channels, rate, false); var d = new float[frames * channels]; for (int i = 0; i < d.Length; i++) d[i] = (i % 7 - 3) / 10f; c.SetData(d, 0); return c; }
    sealed class Fixture : IDisposable
    {
        public readonly PersonalLearningCaptureLeases Leases = new PersonalLearningCaptureLeases();
        public readonly PersonalSingingLearningRecorder Recorder = new PersonalSingingLearningRecorder();
        public readonly List<byte[]> Audio = new List<byte[]>();
        public string Root, Parent, Lineage;
        public int Serial;
        public Fixture()
        {
            Leases.SetEnabled(true);
            PersonalSingingLearningCapture.Started += Start;
            PersonalSingingLearningCapture.Completed += Complete;
            PersonalSingingLearningCapture.Aborted += Abort;
            PersonalSingingLearningCapture.ActiveCapture += Active;
        }
        void Start(string root, int serial) { Root = root; Serial = serial; Leases.Begin(root, serial, "person", "session", "song"); }
        void Abort(string root, int serial) => Leases.Abort(root, serial);
        bool Active(string root, int serial) => Leases.Contains(root, serial);
        void Complete(byte[] wav, string root, int serial, string parent, string lineage)
        { if (!Leases.TrySeal(root, serial, out var lease)) throw new Exception("missing lease"); Audio.Add(wav); Parent = parent; Lineage = lineage; }
        public void Dispose()
        {
            PersonalSingingLearningCapture.Started -= Start;
            PersonalSingingLearningCapture.Completed -= Complete;
            PersonalSingingLearningCapture.Aborted -= Abort;
            PersonalSingingLearningCapture.ActiveCapture -= Active;
        }
    }
    static void Stereo()
    {
        float[] pcm = { -.8f, .7f, -.6f, .5f, -.4f, .3f, -.2f, .1f };
        byte[] wav = PersonalSingingLearningRecorder.EncodePcm16(pcm, 2, 48000);
        Check(PersonalSingingLearningProtocol.TryWave(wav, out var rate, out var channels, out var frames, out var offset, out _), "stereo parse");
        Check(rate == 48000 && channels == 2 && frames == 4, "stereo source coordinates");
        Check(wav.Length == 44 + pcm.Length * 2 && BitConverter.ToInt32(wav, 40) == pcm.Length * 2, "stereo payload complete");
        for (int i = 0; i < pcm.Length; i++) Check(BitConverter.ToInt16(wav, offset + i * 2) == (short)(pcm[i] * 32767), "channel sample lost");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        { bool rejected = false; try { PersonalSingingLearningRecorder.EncodePcm16(new[] { invalid }, 1, 16000); } catch (ArgumentException) { rejected = true; } Check(rejected, "nonfinite PCM accepted"); }
    }
    static void OnceAndRaw()
    {
        using (var f = new Fixture())
        {
            var c = Clip(); f.Recorder.Seal(c); Check(f.Audio.Count == 0, "seal without capture");
            var lineage = JObject.Parse("{\"known\":true,\"regions\":[{\"sourceId\":\"pcm:parent:1\"},{\"sourceId\":\"pcm:child:2\"}]}");
            f.Recorder.Begin(); f.Recorder.Seal(c, lineage); f.Recorder.Seal(c, lineage);
            Check(f.Audio.Count == 1 && c.ReadCalls == 1, "seal must copy and publish exactly once");
            Check(f.Parent == "pcm:parent:1" && JToken.Parse(f.Lineage)["regions"].Count() == 2, "original lineage lost");
            lineage["regions"][0]["sourceId"] = "changed";
            Check((string)JToken.Parse(f.Lineage)["regions"][0]["sourceId"] == "pcm:parent:1", "lineage not frozen");
            Check(!f.Leases.Contains(f.Root, f.Serial), "sealed lease retained");
            Check(PersonalSingingLearningProtocol.TryWave(f.Audio[0], out var rate, out _, out var frames, out _, out _) && frames == 160 && rate == 16000, "short/noise recording was filtered");
            f.Recorder.Begin(); f.Recorder.Seal(c); Check(f.Audio.Count == 2 && f.Serial == 2, "next capture not independent");
        }
    }
    static void FailureCleanup()
    {
        using (var f = new Fixture())
        {
            for (int i = 0; i < 100; i++) { f.Recorder.Begin(); f.Recorder.Seal(null); }
            f.Recorder.Begin(); f.Recorder.Seal(Clip()); Check(f.Audio.Count == 1, "null captures exhausted 64 leases");
            var bad = Clip(); bad.FailRead = true; f.Recorder.Begin(); f.Recorder.Seal(bad);
            Check(f.Audio.Count == 1 && !f.Leases.Contains(f.Root, f.Serial), "failed read leaked or published capture");
            var longClip = Clip(8000 * 121, 1, 8000); f.Recorder.Begin(); f.Recorder.Seal(longClip);
            Check(longClip.ReadCalls == 0 && f.Audio.Count == 1 && !f.Leases.Contains(f.Root, f.Serial), "long clip truncated or lease retained");
            f.Recorder.Begin(); f.Leases.SetEnabled(false); var off = Clip(); f.Recorder.Seal(off);
            Check(off.ReadCalls == 0 && f.Audio.Count == 1, "disabled capture copied/uploaded");
        }
    }
    static void SubscriberIsolation()
    {
        Action<string,int> boomStart = (r,c) => throw new Exception("start subscriber");
        Action<string,int> boomAbort = (r,c) => throw new Exception("abort subscriber");
        Action<byte[],string,int,string,string> boomComplete = (a,r,c,p,l) => throw new Exception("completed subscriber");
        Func<string,int,bool> boomActive = (r,c) => throw new Exception("active subscriber");
        Func<bool> boomReady = () => throw new Exception("readiness subscriber"); Func<bool> yes = () => true;
        PersonalSingingLearningCapture.Started += boomStart; PersonalSingingLearningCapture.Completed += boomComplete;
        PersonalSingingLearningCapture.Aborted += boomAbort; PersonalSingingLearningCapture.ActiveCapture += boomActive;
        PersonalSingingLearningCapture.Readiness += boomReady; PersonalSingingLearningCapture.Readiness += yes;
        try
        {
            using(var f = new Fixture())
            { Check(PersonalSingingLearningCapture.WantsCapture, "failed readiness prevents next subscriber"); f.Recorder.Begin(); f.Recorder.Seal(Clip()); Check(f.Audio.Count == 1, "failed subscriber prevented capture"); }
        }
        finally
        {
            PersonalSingingLearningCapture.Started -= boomStart; PersonalSingingLearningCapture.Completed -= boomComplete;
            PersonalSingingLearningCapture.Aborted -= boomAbort; PersonalSingingLearningCapture.ActiveCapture -= boomActive;
            PersonalSingingLearningCapture.Readiness -= boomReady; PersonalSingingLearningCapture.Readiness -= yes;
        }
    }
    static void LegacyVoiceInputs()
    {
        using (var f = new Fixture())
        {
            Microphone.Next = Clip(16000); Microphone.Position = 320; var legacy = new VoiceInputs(); legacy.StartRecordAudio();
            AudioClip submitted = null; legacy.StopRecordAudio(c => submitted = c);
            Check(submitted != null && submitted.samples == 320 && f.Audio.Count == 1, "legacy final PCM and ASR handoff differ");
            Check(PersonalSingingLearningProtocol.TryWave(f.Audio[0], out _, out _, out var frames, out _, out _) && frames == 320, "legacy unused buffer zeros retained");
            Microphone.Next = Clip(16000); Microphone.Position = 0; legacy.StartRecordAudio(); legacy.StopRecordAudio(c => submitted = c);
            Check(submitted != null, "legacy ASR fallback changed");
            Check(f.Audio.Count == 1, "invalid mic cursor called padded full buffer original PCM");
            Check(!f.Leases.Contains(f.Root, f.Serial), "invalid mic cursor lease not retired");
        }
    }
    static void IncrementalPitch()
    {
        var packets = new List<byte[]>(); var offsets = new List<int>(); int cancelled = 0;
        Action<byte[],string,int,int> receive = (pcm,r,c,start) => { packets.Add(pcm); offsets.Add(start); };
        Action<string,int> cancel = (r,c) => cancelled++;
        Action<byte[],string,int,int> broken = (pcm,r,c,start) => throw new Exception("optional pitch subscriber");
        PersonalSingingLearningCapture.PitchChunk += broken;
        PersonalSingingLearningCapture.PitchChunk += receive;
        PersonalSingingLearningCapture.PitchCancelled += cancel;
        try
        {
            using (var f = new Fixture())
            {
                var source = Enumerable.Range(0, 32991).Select(i => (i % 103 - 51) / 40f).ToArray();
                f.Recorder.PushPitch(source, 1, 16000); Check(packets.Count == 0, "pitch without active capture");
                f.Recorder.Begin();
                f.Recorder.PushPitch(source.Take(79).ToArray(), 1, 16000);
                f.Recorder.PushPitch(source.Skip(79).ToArray(), 1, 16000);
                var clip = AudioClip.Create("stream", source.Length, 1, 16000, false); clip.SetData(source, 0);
                f.Recorder.Seal(clip);
                Check(packets.Count == 4 && packets.All(x => x.Length <= 32000), "bounded pitch packets");
                Check(offsets.SequenceEqual(new[] {0,79,16079,32079}), "source offsets not contiguous");
                Check(packets.SelectMany(x => x).SequenceEqual(f.Audio[0].Skip(44)), "stream PCM differs from authoritative WAV quantization");
                int count = packets.Count;
                f.Recorder.PushPitch(new float[32], 1, 16000); Check(packets.Count == count, "pitch after seal");
                f.Recorder.Begin(); f.Recorder.PushPitch(new float[32], 1, 16000);
                Check(offsets.Last() == 0, "new capture did not reset pitch origin");
                f.Recorder.CancelPitch(); f.Recorder.PushPitch(new float[32], 1, 16000);
                Check(packets.Count == count + 1, "cancelled pitch continued");
                f.Recorder.Seal(Clip()); Check(f.Audio.Count == 2, "optional pitch cancellation lost original");
                f.Recorder.Begin(); f.Recorder.PushPitch(new float[32], 2, 48000);
                f.Recorder.Seal(Clip(160,2,48000)); Check(f.Audio.Count == 3 && cancelled == 2, "unsupported pitch format affected original capture");
                f.Recorder.Begin(); f.Recorder.PushPitch(new[] {float.NaN}, 1, 16000);
                Check(cancelled == 3 && !f.Recorder.WantsPitch, "nonfinite pitch did not isolate stream");
                f.Recorder.Seal(Clip()); Check(f.Audio.Count == 4, "pitch failure blocked valid authoritative original");
                f.Recorder.Begin(); f.Leases.SetEnabled(false); f.Recorder.PushPitch(new float[32], 1, 16000);
                Check(packets.Count == count + 1, "disabled collection still streamed pitch");
            }
            packets.Clear(); offsets.Clear();
            using (var f = new Fixture())
            {
                Microphone.Next = Clip(16000); Microphone.Position = 329; var legacy = new VoiceInputs();
                legacy.StartRecordAudio(); legacy.StopRecordAudio(_ => {});
                Check(packets.SelectMany(x => x).SequenceEqual(f.Audio.Single().Skip(44)), "PTT final pitch flush differs from saved original");
            }
        }
        finally
        {
            PersonalSingingLearningCapture.PitchChunk -= broken;
            PersonalSingingLearningCapture.PitchChunk -= receive;
            PersonalSingingLearningCapture.PitchCancelled -= cancel;
        }
    }
    public static void Main()
    {
        Stereo(); OnceAndRaw(); FailureCleanup(); SubscriberIsolation(); LegacyVoiceInputs(); IncrementalPitch();
        Console.WriteLine("PASS " + checks + " checks; actual recorder/bus/protocol/VoiceInputs; Unity data API stub only; no network/audio.");
    }
}
