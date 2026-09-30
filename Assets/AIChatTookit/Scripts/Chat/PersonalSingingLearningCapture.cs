using System;

// Raw capture notification only. No microphone, model decision or automatic label.
public static class PersonalSingingLearningCapture
{
    public static event Action<string, int> Started;
    public static event Action<byte[], string, int, string, string> Completed;
    public static event Action<string, int> Aborted;
    // Optional, best-effort PCM16 stream. Original WAV remains the source of truth.
    public static event Action<byte[], string, int, int> PitchChunk;
    public static event Action<string, int> PitchCancelled;
    public static event Func<bool> Readiness;
    public static event Func<string, int, bool> ActiveCapture;
    public static bool HasListeners => Completed != null;
    public static bool WantsCapture
    {
        get { if (Readiness != null) foreach (Func<bool> ready in Readiness.GetInvocationList()) try { if (ready()) return true; } catch (Exception e) { Report(e); } return false; }
    }
    public static bool IsCaptureActive(string sessionRoot, int captureSerial)
    {
        if (ActiveCapture != null) foreach (Func<string, int, bool> active in ActiveCapture.GetInvocationList())
            try { if (active(sessionRoot, captureSerial)) return true; } catch (Exception e) { Report(e); }
        return false;
    }

    public static void BeginCapture(string sessionRoot, int captureSerial)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot) || captureSerial <= 0) return;
        if (Started != null) foreach (Action<string, int> receive in Started.GetInvocationList())
            try { receive(sessionRoot, captureSerial); } catch (Exception e) { Report(e); }
    }

    public static void Publish(byte[] wav, string sessionRoot, int captureSerial, string parentId, string lineageJson)
    {
        if (wav == null || wav.Length <= 44 || string.IsNullOrWhiteSpace(sessionRoot) || captureSerial <= 0)
        { AbortCapture(sessionRoot, captureSerial); return; }
        if (Completed != null) foreach (Action<byte[], string, int, string, string> receive in Completed.GetInvocationList())
            try { receive(wav, sessionRoot, captureSerial, parentId, lineageJson); } catch (Exception e) { Report(e); }
    }
    public static void AbortCapture(string sessionRoot, int captureSerial)
    {
        if (Aborted != null) foreach (Action<string, int> receive in Aborted.GetInvocationList())
            try { receive(sessionRoot, captureSerial); } catch (Exception e) { Report(e); }
    }
    public static void PublishPitch(byte[] pcm16, string root, int serial, int startSample)
    {
        if (PitchChunk != null) foreach (Action<byte[], string, int, int> receive in PitchChunk.GetInvocationList())
            try { receive(pcm16, root, serial, startSample); } catch (Exception e) { Report(e); }
    }
    public static void CancelPitch(string root, int serial)
    {
        if (PitchCancelled != null) foreach (Action<string, int> receive in PitchCancelled.GetInvocationList())
            try { receive(root, serial); } catch (Exception e) { Report(e); }
    }
    private static void Report(Exception error) => System.Diagnostics.Trace.TraceError("Personal learning subscriber failed: " + error.Message);
}
