namespace SpreadTrader.Core.Realtime;

/// <summary>
/// Tracks how many connected browser tabs currently have the Positions &amp; PNL page open, so
/// LivePositionsHostedService can skip its Kite REST resync and SignalR push work entirely while
/// nobody is actually watching, instead of running that loop unconditionally for the whole app
/// session regardless of which page anyone's on. Updated from SpreadHub.JoinPositions/
/// LeavePositions (and its OnDisconnectedAsync cleanup, in case a tab closes without an explicit
/// "leave" call) - see hooks/spreadHub.js's joinPositionsView/leavePositionsView on the frontend,
/// called from PositionsPage's mount/unmount.
///
/// Registered as a singleton so the same instance is shared between the SignalR hub (which only
/// knows about individual connections) and the hosted service (which only cares whether the count
/// is currently above zero) - neither needs to know about the other.
/// </summary>
public sealed class PositionsViewerTracker
{
    private int _count;

    public bool HasViewers => Volatile.Read(ref _count) > 0;

    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>Floors at zero rather than going negative - a defensive guard against a
    /// double-decrement race (e.g. an explicit LeavePositions call landing at nearly the same
    /// moment as OnDisconnectedAsync's own cleanup for the same connection), which would
    /// otherwise be able to push the count negative and make HasViewers report false while a
    /// real viewer is still connected.</summary>
    public void Decrement()
    {
        int current, updated;
        do
        {
            current = Volatile.Read(ref _count);
            updated = current > 0 ? current - 1 : 0;
        } while (Interlocked.CompareExchange(ref _count, updated, current) != current);
    }
}
