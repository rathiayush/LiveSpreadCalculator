namespace SpreadTrader.Core.Brokers;

/// <summary>
/// A one-shot REST fallback for "what is this instrument's price right now", used ONLY to unblock
/// CdvHostedService's ATM resolution when the live feed hasn't ticked yet (e.g. just after a
/// restart, before the WebSocket subscription has caught up) - never as an ongoing price source.
/// Every price after this one bootstrap call still comes from the live feed exactly as before; see
/// CdvHostedService's per-underlying "already bootstrapped today" guard, which ensures this is
/// called at most once per underlying per trading day.
///
/// Deliberately its own narrow interface (in Core, Dhan-only in practice) rather than a general
/// "get quote" API, so its one-shot-bootstrap-only contract stays obvious at every call site.
/// </summary>
public interface ISpotPriceBootstrap
{
    /// <summary>Dhan's Market Quote LTP REST endpoint. Returns null on any failure (not
    /// authenticated, network error, instrument not found in the response) - callers treat that
    /// exactly like "still waiting for a live tick", not as an error to surface.</summary>
    Task<decimal?> GetLastTradedPriceAsync(uint instrumentToken, string brokerSegment, CancellationToken cancellationToken);
}
