using System;

// Recovery only reacquires local samples. It never replays a remote request,
// interpolates across a gap, changes the anchor, or acquires another owner.
namespace NeEEvA.Motion
{
public sealed class ArdyPoseHistoryWindow
{
    public const float MaximumGap = .15f, MaximumElapsed = 4f;
    public const int MaximumRestarts = 2;
    public readonly float StartedAt;
    public int Restarts { get; private set; }
    public ArdyPoseHistoryWindow(float startedAt) { StartedAt = startedAt; }
    public bool Expired(float now) => float.IsNaN(now) || float.IsInfinity(now) || now - StartedAt > MaximumElapsed;
    public bool TryRestart(float now, float gap, bool ownerAndAnchorValid)
    {
        if(!ownerAndAnchorValid || Expired(now) || gap <= MaximumGap || float.IsNaN(gap) ||
            float.IsInfinity(gap) || Restarts >= MaximumRestarts)return false;
        Restarts++;return true;
    }
}

}
