using Microsoft.AspNetCore.SignalR;
using SpreadTrader.Core.Realtime;

namespace LiveSpreadCalculator.Hubs;

/// <summary>
/// Mostly push-only hub - the server broadcasts "spreadUpdate", "triggerFired", "basketExecuted",
/// "kiteAuthInvalidated", and "positionsUpdate" events to every connected client (see
/// SignalRSpreadPublisher / SignalRTriggerEventSink / SignalROrderExecutionSink /
/// SignalRKiteAuthNotifier / SignalRPositionsPublisher). All configuration still goes through the
/// REST controllers - the two client-invoked methods below exist purely so
/// LivePositionsHostedService knows whether anyone's actually looking at Positions &amp; PNL right
/// now, not to carry any real data.
/// </summary>
public sealed class SpreadHub : Hub
{
    // Set in Context.Items (per-connection storage SignalR already provides) rather than a plain
    // field on this class - a new Hub instance is constructed per method call/connection, so an
    // instance field wouldn't persist between JoinPositions and the later OnDisconnectedAsync
    // cleanup call anyway. This also means a duplicate JoinPositions call (e.g. React re-running
    // an effect) is naturally idempotent - see JoinPositions below.
    private const string JoinedPositionsKey = "JoinedPositions";

    private readonly PositionsViewerTracker _positionsViewers;

    public SpreadHub(PositionsViewerTracker positionsViewers)
    {
        _positionsViewers = positionsViewers;
    }

    /// <summary>Called once when the Positions &amp; PNL page mounts (see
    /// hooks/spreadHub.js's joinPositionsView, wired to PositionsPage) - and again after any
    /// SignalR reconnect, since a reconnect gets a brand new connection/Context.Items on the
    /// server, wiping out whatever this connection had joined before. Idempotent: calling it
    /// twice on the same connection (a stray double-mount, for instance) does not double-count.</summary>
    public Task JoinPositions()
    {
        if (!Context.Items.ContainsKey(JoinedPositionsKey))
        {
            Context.Items[JoinedPositionsKey] = true;
            _positionsViewers.Increment();
        }
        return Task.CompletedTask;
    }

    /// <summary>Called when the Positions &amp; PNL page unmounts (navigating away while still
    /// connected) - OnDisconnectedAsync below handles the other case (tab closed/connection
    /// dropped entirely without an explicit unmount, e.g. closing the browser tab).</summary>
    public Task LeavePositions()
    {
        if (Context.Items.Remove(JoinedPositionsKey))
            _positionsViewers.Decrement();
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(JoinedPositionsKey))
            _positionsViewers.Decrement();
        return base.OnDisconnectedAsync(exception);
    }
}
