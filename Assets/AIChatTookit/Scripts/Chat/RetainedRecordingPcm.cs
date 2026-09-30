using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

// Session disk backing for retained immutable WAVs. Callers still own transient
// working buffers; the resident limit applies only to this archive cache.
public sealed class RetainedRecordingPcmStore
{
    public static readonly RetainedRecordingPcmStore Shared = new RetainedRecordingPcmStore(
        Path.Combine(Path.GetTempPath(), "NeEEvA-recording-archive", Guid.NewGuid().ToString("N")), 64L * 1024 * 1024);
    private readonly string directory;
    private readonly long maximumResidentBytes;
    private readonly LinkedList<RetainedRecordingPcm> resident = new LinkedList<RetainedRecordingPcm>();
    private long residentBytes;
    public long ResidentBytes => residentBytes;
    public string LastError { get; private set; }
    public Action<string> OnError;

    public RetainedRecordingPcmStore(string directory, long maximumResidentBytes)
    {
        this.directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
        if (maximumResidentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumResidentBytes));
        this.maximumResidentBytes = maximumResidentBytes;
    }

    public RetainedRecordingPcm Put(byte[] bytes)
    {
        if (bytes == null) return null;
        var result = new RetainedRecordingPcm(this, bytes, Digest(bytes));
        try
        {
            Directory.CreateDirectory(directory);
            result.Path = Path.Combine(directory, result.Digest + ".wav");
            if (!File.Exists(result.Path)) File.WriteAllBytes(result.Path, bytes);
            result.Persisted = true;
            Admit(result, bytes);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
        {
            Error("recording-archive-write-failed: " + error.Message);
            // No old source is deleted to make room. A bounded in-memory fallback
            // can retain this source; otherwise its explicit unavailable state
            // prevents later preparation from claiming it still exists.
            if (residentBytes + bytes.Length <= maximumResidentBytes) Admit(result, bytes);
            else { result.UnavailableReason = "archive-write-failed-and-resident-limit"; result.Initial = null; }
        }
        return result;
    }

    internal byte[] Read(RetainedRecordingPcm value)
    {
        if (value.Bytes != null)
        { Touch(value); return value.Bytes; }
        if (!value.Persisted) return null;
        try
        {
            var bytes = File.ReadAllBytes(value.Path);
            if (bytes.Length != value.ByteCount || Digest(bytes) != value.Digest)
            { value.UnavailableReason = "archive-pcm-integrity-failed"; Error(value.UnavailableReason); return null; }
            value.Restore?.Invoke(bytes);
            Admit(value, bytes); return bytes;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
        { value.UnavailableReason = "archive-pcm-read-failed"; Error(value.UnavailableReason + ": " + error.Message); return null; }
    }

    private void Admit(RetainedRecordingPcm value, byte[] bytes)
    {
        while (residentBytes + bytes.Length > maximumResidentBytes && resident.Count > 0)
        {
            var victim = resident.FirstOrDefault(v => v.Persisted);
            if (victim == null) break; // Failed-disk fallback data cannot be evicted.
            victim.BeforeEvict?.Invoke(victim.Bytes);
            residentBytes -= victim.Bytes.LongLength;
            resident.Remove(victim.Node); victim.Node = null; victim.Bytes = null; victim.Initial = null;
        }
        value.Initial = null;
        if (residentBytes + bytes.Length > maximumResidentBytes) return;
        value.Bytes = bytes; residentBytes += bytes.LongLength;
        value.Node = resident.AddLast(value);
    }

    private void Touch(RetainedRecordingPcm value)
    { if (value.Node != null) { resident.Remove(value.Node); value.Node = resident.AddLast(value); } }
    private void Error(string message) { LastError = message; OnError?.Invoke(message); }
    private static string Digest(byte[] value)
    { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
}

public sealed class RetainedRecordingPcm
{
    private readonly RetainedRecordingPcmStore store;
    internal byte[] Bytes;
    internal byte[] Initial;
    internal string Path;
    internal string Digest;
    internal bool Persisted;
    internal LinkedListNode<RetainedRecordingPcm> Node;
    public long ByteCount { get; private set; }
    public double DurationSeconds { get; internal set; }
    public string UnavailableReason { get; internal set; }
    public bool Available => UnavailableReason == null && (Bytes != null || Persisted && File.Exists(Path));
    public Action<byte[]> BeforeEvict;
    public Action<byte[]> Restore;
    internal RetainedRecordingPcm(RetainedRecordingPcmStore store, byte[] bytes, string digest)
    { this.store = store; Initial = bytes; ByteCount = bytes.LongLength; Digest = digest; }
    public byte[] Read() => store.Read(this);
}
