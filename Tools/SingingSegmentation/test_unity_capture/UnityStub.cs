using System;
using System.Collections.Generic;

// Only the Unity data access API is stubbed. Recorder, event bus, lease and
// legacy recorder source are compiled directly from production paths.
namespace UnityEngine
{
    public class MonoBehaviour { }
    public static class Time { public static float unscaledTime; }
    public sealed class SerializeField : Attribute { }
    public static class Debug
    {
        public static readonly List<string> Warnings = new List<string>();
        public static void LogWarning(object value) => Warnings.Add(value?.ToString());
    }
    public sealed class AudioClip
    {
        private float[] data;
        public int samples { get; private set; }
        public int channels { get; private set; }
        public int frequency { get; private set; }
        public float length => samples / (float)frequency;
        public bool FailRead;
        public int ReadCalls;
        public static AudioClip Create(string name, int frames, int channels, int frequency, bool stream)
            => new AudioClip { samples = frames, channels = channels, frequency = frequency, data = new float[frames * channels] };
        public bool SetData(float[] values, int offset)
        { Array.Copy(values, 0, data, offset * channels, values.Length); return true; }
        public bool GetData(float[] values, int offset)
        { ReadCalls++; if (FailRead) return false; Array.Copy(data, offset * channels, values, 0, values.Length); return true; }
    }
    public static class Microphone
    {
        public static AudioClip Next;
        public static int Position;
        public static bool Ended;
        public static AudioClip Start(string device, bool loop, int length, int frequency) { Ended = false; return Next; }
        public static int GetPosition(string device) => Ended ? 0 : Position;
        public static void End(string device) { Ended = true; }
    }
}
public sealed class SignalManager { }
