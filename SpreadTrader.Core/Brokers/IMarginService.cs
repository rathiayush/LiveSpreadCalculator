using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Brokers;

/// <summary>Usable margin available in the account right now - see
/// <see cref="IMarginService.GetAvailableMarginAsync"/>.</summary>
public sealed record AvailableMarginResult(bool Success, decimal? Net, string? ErrorMessage);

/// <summary>
/// Pre-trade margin checks. Both calls exist so OrderDispatchHostedService can refuse to punch a
/// basket it can't fully afford, rather than discovering that leg-by-leg and ending up with a
/// naked, unhedged position because the second leg was rejected for margin.
/// </summary>
public interface IMarginService
{
    /// <summary>What a basket of legs would cost in margin WITH the exchange's hedge/spread
    /// benefit applied when the legs offset each other (which is the whole point for a calendar
    /// spread - the hedged figure is a fraction of the sum of the naked legs). Implementations
    /// must ask the broker to consider the account's existing positions/orders, so "final" is the
    /// incremental margin this basket needs on top of what is already blocked - the correct figure
    /// to compare against available margin.</summary>
    Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken);

    /// <summary>The account's actual usable margin (cash + collateral, net of what open positions
    /// and orders already block) for the given settlement segment - "equity" for NSE F&amp;O,
    /// "commodity" for MCX. Implementations map that neutral segment name onto whatever their
    /// platform calls it.</summary>
    Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default);
}
