using System;
using System.Collections.Generic;

namespace AIChat.Agent
{
    /// <summary>A single review deadline, independent of speech fatigue. Callers supply a monotonic active clock.</summary>
    public sealed class SceneReviewClock
    {
        private readonly Queue<double> requests = new Queue<double>();
        public bool Pending { get; private set; }
        public double Deadline { get; private set; }
        public double ScheduledAt { get; private set; }
        public double RequestedSeconds { get; private set; }
        public string Reason { get; private set; } = "";
        public string LatestReason { get; private set; } = "";
        public int CoalescedEvents { get; private set; }
        public bool PriorityEvent { get; private set; }
        public double LastActualElapsed { get; private set; }
        public double LastQueueDelay { get; private set; }
        public string LastWakeReason { get; private set; } = "";
        public double LastRequestAt { get; private set; } = -1;
        public int RequestsInLastMinute(double now) { Trim(now); return requests.Count; }
        public void Clear(bool budget = false)
        {
            Pending = false; Reason = ""; LatestReason = ""; CoalescedEvents = 0; PriorityEvent = false;
            if (budget) { requests.Clear(); LastRequestAt = -1; }
        }
        public void Schedule(double now, double seconds, string reason, bool priority)
        {
            seconds = double.IsNaN(seconds) || double.IsInfinity(seconds) ? 30 : Math.Max(.1, Math.Min(600, seconds));
            // A normal reply finishing cannot erase an already queued action result.
            if (Pending && PriorityEvent && !priority) return;
            if (Pending && priority && PriorityEvent && Deadline <= now + seconds)
            { LatestReason = reason ?? "scheduled"; CoalescedEvents++; return; }
            // Repeated scheduling of the same normal review cannot push its
            // already queued deadline forward indefinitely.
            if (Pending && !priority && !PriorityEvent && Reason == (reason ?? "scheduled") && Deadline <= now + seconds)
            { CoalescedEvents++; return; }
            double due = Pending && priority ? Math.Min(Deadline, now + seconds) : now + seconds;
            bool merging = Pending && priority && PriorityEvent;
            double queuedAt = merging ? ScheduledAt : now;
            CoalescedEvents = merging ? CoalescedEvents + 1 : 0;
            Pending = true; Deadline = due; ScheduledAt = queuedAt;
            RequestedSeconds = seconds; Reason = reason ?? "scheduled"; LatestReason = Reason; PriorityEvent = priority;
        }
        public bool IsDue(double now) => Pending && now >= Deadline;
        public bool DiscardSupersededSceneAction(int actionUserTurn, int currentUserTurn)
        {
            if (!Pending || actionUserTurn == currentUserTurn ||
                !Reason.StartsWith("scene-action-", StringComparison.Ordinal)) return false;
            Clear(); // Keep the request budget; only this obsolete wake is removed.
            return true;
        }
        public bool CanRequest(double now, int budget, double minimumSpacing, out double readyAt)
        {
            Trim(now); readyAt = now;
            if (LastRequestAt >= 0) readyAt = Math.Max(readyAt, LastRequestAt + minimumSpacing);
            if (requests.Count >= Math.Max(1, budget)) readyAt = Math.Max(readyAt, requests.Peek() + 60);
            return readyAt <= now;
        }
        public void RecordRequest(double now)
        {
            Trim(now); requests.Enqueue(now); LastRequestAt = now;
        }
        public string Consume(double now)
        {
            string reason = Reason;
            LastActualElapsed = Math.Max(0, now - ScheduledAt);
            LastQueueDelay = Math.Max(0, now - Deadline);
            LastWakeReason = reason; Clear(); return reason;
        }
        private void Trim(double now) { while (requests.Count > 0 && now - requests.Peek() >= 60) requests.Dequeue(); }
    }
}
