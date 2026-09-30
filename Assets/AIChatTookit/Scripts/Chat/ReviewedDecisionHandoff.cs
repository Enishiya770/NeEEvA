using System;

/// <summary>Transfers a reviewed candidate to one fresh formal decision, never directly to an action.</summary>
public sealed class ReviewedDecisionHandoff<T> where T : class
{
    private bool started, completed;
    private readonly Func<bool> current;
    private readonly Func<T, bool> fresh;
    private readonly Action<T, bool> dispatch;
    private readonly Action<string> ended;

    public ReviewedDecisionHandoff(Func<bool> current, Func<T, bool> fresh,
        Action<T, bool> dispatch, Action<string> ended)
    { this.current = current; this.fresh = fresh; this.dispatch = dispatch; this.ended = ended; }

    public void Start(T observation, Action<Action<T>> refresh)
    {
        if (started || completed) return;
        started = true;
        if (!current()) { Cancel("owner_changed"); return; }
        if (observation == null || fresh(observation)) { Complete(observation, false); return; }
        // Refresh at most once. Capture failure cannot recycle a stale image or trigger another review.
        refresh(value => Complete(value, true));
    }

    private void Complete(T observation, bool refreshed)
    {
        if (completed) return;
        if (!current()) { Cancel("owner_changed"); return; }
        var usable = observation != null && fresh(observation) ? observation : null;
        completed = true; // A duplicate or synchronous capture callback cannot dispatch twice.
        try { dispatch(usable, refreshed); }
        finally { ended(usable == null ? "dispatched_without_image" : "dispatched_fresh"); }
    }

    public void Cancel(string reason)
    {
        if (completed) return;
        completed = true;
        ended(reason);
    }

    public static bool NeedsCancellationReply(bool wasPending, string transition, string review,
        bool withheld, bool sameSpeechRound, int goalVersion, int alreadyScheduledVersion)
        => wasPending && transition == "cancel" && review == "cancelled" && withheld &&
            sameSpeechRound && goalVersion >= 0 && goalVersion != alreadyScheduledVersion;
}
