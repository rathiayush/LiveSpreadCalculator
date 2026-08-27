namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Declares instrument tokens that something OUTSIDE the spread/position trading path needs kept
/// subscribed on the live feed.
///
/// This exists because TickIngestionHostedService's reconciliation loop recomputes "what does this
/// process actually need ticks for" from scratch every few minutes and unsubscribes everything
/// else - deliberately, so a missed call site anywhere can never leak a subscription. That rule is
/// correct for the trading engine, but it means any other feature that subscribes its own
/// instruments has them silently dropped within minutes, and its data quietly goes flat.
///
/// Rather than teach the reconciler about each such feature (which would put a reference to every
/// add-on inside the core tick path), it simply unions in whatever the registered implementations
/// of this interface report. Nothing is registered by default, so with no add-ons enabled the
/// reconciler behaves exactly as it always has.
///
/// Implementations must be cheap and non-blocking - this is called on the reconciliation timer and
/// must never do I/O.
/// </summary>
public interface IExtraTickSubscriber
{
    /// <summary>Tokens that must stay subscribed right now. An empty collection is normal (the
    /// feature is enabled but hasn't resolved its instruments yet) and simply contributes nothing.</summary>
    IReadOnlyCollection<uint> GetRequiredTokens();
}
