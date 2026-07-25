using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Brokers;

/// <summary>
/// The live market-data connection: one long-lived socket to the broker, subscribed to a set of
/// instrument tokens, raising a <see cref="Tick"/> for every update. Implementations own their own
/// reconnect/resubscribe behaviour - <see cref="RunAsync"/> is expected to block for the process
/// lifetime and recover from failures internally rather than returning on a dropped connection.
///
/// IMPORTANT: exactly one instance of this must run per deployment. Both brokers cap the number of
/// concurrent feed connections per account, and - far more importantly - a second instance would
/// independently evaluate the same trigger rules against the same ticks and place the same orders
/// twice. See ARCHITECTURE.md §8; do not scale the host process out horizontally.
/// </summary>
public interface IMarketDataFeed : IAsyncDisposable
{
    /// <summary>Raised for every market-data update received. Called on the socket's receive loop,
    /// so handlers must be cheap and non-blocking - the standard consumer is
    /// <c>ITickStore.Update</c>, a dictionary write.</summary>
    event Action<Tick>? TickReceived;

    /// <summary>Raised whenever the broker reports that SOME order belonging to this account
    /// changed state - this app's own triggered baskets, a manual square-off, or a trade placed
    /// directly in the broker's own app. Deliberately parameterless: the only consumer
    /// (PositionsCache) doesn't need to know which order changed, just that it should re-fetch
    /// positions now instead of waiting for its next scheduled poll.
    ///
    /// Kite delivers these as text frames on the same ticker socket; Dhan has a separate
    /// order-update socket which its implementation runs alongside the feed. Either way consumers
    /// see one event.</summary>
    event Action? OrderUpdateReceived;

    /// <summary>Adds tokens to the live subscription. Safe to call at any time, including before
    /// the socket is connected and while it is running - new tokens are remembered and
    /// re-subscribed after any reconnect.</summary>
    Task SubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken);

    /// <summary>Removes tokens from the live subscription so a contract nothing needs any more
    /// stops consuming bandwidth and a tick-store slot - see
    /// TickIngestionHostedService's reconciliation loop, the only caller.</summary>
    Task UnsubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken);

    /// <summary>Every token currently remembered as subscribed (survives reconnects). Read by the
    /// reconciliation loop to find tokens nothing needs any more.</summary>
    IReadOnlyCollection<uint> GetSubscribedTokens();

    /// <summary>Connects, subscribes, and runs until cancelled, reconnecting with backoff on any
    /// failure. Does not return on a dropped connection.</summary>
    Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken cancellationToken);
}
