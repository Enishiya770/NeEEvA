using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

/// <summary>Request-local, content-free evidence for streams that never reach public speech.</summary>
public sealed class FormalStreamProgress
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public double Seconds => clock.Elapsed.TotalSeconds;
    public long Bytes { get; private set; }
    public int Events { get; private set; }
    public int ParseFailures { get; private set; }
    public long ContentCharacters { get; private set; }
    public long ReasoningCharacters { get; private set; }
    public double? LastBytesSeconds { get; private set; }
    public double? LastDeltaSeconds { get; private set; }
    public bool Done { get; private set; }
    public void Receive(int bytes) { Bytes += bytes; LastBytesSeconds = Seconds; }
    public void Event() { Events++; }
    public void ParseFailure() { ParseFailures++; }
    public void MarkDone() { Done = true; }
    public void Delta(string content, string reasoning)
    {
        ContentCharacters += content?.Length ?? 0;
        ReasoningCharacters += reasoning?.Length ?? 0;
        if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoning)) LastDeltaSeconds = Seconds;
    }
    public JObject Snapshot(int pendingLineCharacters) => new JObject {
        ["elapsedSeconds"] = Seconds, ["receivedBytes"] = Bytes, ["sseEvents"] = Events,
        ["parseFailures"] = ParseFailures, ["contentCharacters"] = ContentCharacters,
        ["reasoningCharacters"] = ReasoningCharacters, ["lastBytesSeconds"] = LastBytesSeconds,
        ["lastDeltaSeconds"] = LastDeltaSeconds, ["doneReceived"] = Done,
        ["pendingLineCharacters"] = pendingLineCharacters,
        ["scope"] = "Counts and parser state only; no model text, prompts, images or credentials."
    };

    public static string Deadline(double elapsed, double? lastDelta, bool hasPublicOutput,
        double firstPublicSeconds, double idleSeconds, double totalSeconds)
    {
        if (!hasPublicOutput && elapsed >= firstPublicSeconds) return "no-public-output-deadline";
        if (elapsed >= totalSeconds) return "total-deadline";
        if (lastDelta.HasValue && elapsed - lastDelta.Value >= idleSeconds) return "delta-stalled";
        return null;
    }
}
