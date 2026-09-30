using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

/// <summary>Diagnostic only; never consulted by execution gates. Schema 2 starts at capture when available.</summary>
public sealed class ConversationTrace
{
    private readonly string turnId, spanId = Guid.NewGuid().ToString("N"), parentId, kind;
    private readonly Action<string> sink;
    private readonly HashSet<string> seen = new HashSet<string>();
    private readonly object gate = new object();
    private readonly long origin;
    private readonly TurnTiming timing;
    private int sequence;
    public ConversationTrace(string kind, Action<string> sink)
    { turnId = Guid.NewGuid().ToString("N"); this.kind = kind; this.sink = sink; origin = Stopwatch.GetTimestamp(); timing = new TurnTiming(); }
    private ConversationTrace(ConversationTrace parent, string kind)
    { turnId = parent.turnId; parentId = parent.spanId; origin = parent.origin; sink = parent.sink; this.kind = kind; timing = parent.timing; }
    // Shared by every continuation of this accepted input, never by a new capture.
    // A subpipeline restart or rejected interrupt cannot replace this anchor.
    public double? CaptureCloseToNowMilliseconds => timing.SinceClose(Stopwatch.GetTimestamp());
    // The value freezes at the first actual playback progress, not at a later
    // log call or pipeline restart. Typed turns without a capture stay unknown.
    public bool TryGetFirstFormalAudioLatencySeconds(out double seconds)
        => timing.TryFirstFormalSeconds(out seconds);

    private sealed class TurnTiming
    {
        private readonly object gate = new object();
        private long? closed, formal, opening;
        public double? SinceClose(long now)
        { lock (gate) return closed.HasValue ? (double?)Milliseconds(now - closed.Value) : null; }
        public bool TryFirstFormalSeconds(out double seconds)
        {
            lock (gate)
            {
                seconds = 0;
                if (!closed.HasValue || !formal.HasValue || formal.Value < closed.Value) return false;
                seconds = Milliseconds(formal.Value - closed.Value) / 1000.0;
                return true;
            }
        }
        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        public JObject Observe(string name, JObject source, long now)
        {
            var fields = source == null ? new JObject() : (JObject)source.DeepClone();
            lock (gate)
            {
                if (name == "eot.capture_closed" && !closed.HasValue) closed = now;
                if (name == "audio.formal_playback_advanced")
                {
                    fields["firstFormalForTurn"] = !formal.HasValue;
                    if (!formal.HasValue) formal = now;
                    fields["captureCloseToFirstFormalAudioMs"] = closed.HasValue
                        ? (double?)Milliseconds(formal.Value - closed.Value) : null;
                    fields["latencyScope"] = "original-capture-close-to-AudioSource-progress-including-recovery-not-physical-audibility";
                }
                if (name == "audio.public_filler_started")
                {
                    if (!opening.HasValue) opening = now;
                    fields["captureCloseToOpeningMs"] = closed.HasValue
                        ? (double?)Milliseconds(opening.Value - closed.Value) : null;
                }
                if (name == "singing.finished" && (bool?)fields["completed"] == true)
                    fields["captureCloseToSingingCompletedMs"] = closed.HasValue
                        ? (double?)Milliseconds(now - closed.Value) : null;
            }
            return fields.Count == 0 ? null : fields;
        }
    }
    public ConversationTrace Child(string kind) => new ConversationTrace(this, kind);
    public void Mark(string name, JObject fields = null) => Write(name, fields, true);
    // For bounded sampled streams. Mark remains once per event per span.
    public void Sample(string name, JObject fields = null) => Write(name, fields, false);
    private void Write(string name, JObject fields, bool once)
    {
        try
        {
            lock (gate)
            {
                if (once && !seen.Add(name)) return;
                long now = Stopwatch.GetTimestamp();
                JObject observedFields = timing.Observe(name, fields, now);
                sink?.Invoke(new JObject {
                    ["schema"] = 2, ["turnId"] = turnId, ["spanId"] = spanId,
                    ["parentSpanId"] = parentId, ["kind"] = kind, ["event"] = name,
                    ["sequence"] = ++sequence,
                    ["elapsedMs"] = (now - origin) * 1000.0 / Stopwatch.Frequency,
                    ["utc"] = DateTime.UtcNow.ToString("O"), ["fields"] = observedFields
                }.ToString(Newtonsoft.Json.Formatting.None));
            }
        }
        catch { /* diagnostics must not interrupt dialogue or playback */ }
    }
}
