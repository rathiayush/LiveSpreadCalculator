using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Fetches the account's live open positions and PNL straight from the broker. This exists so the
/// dashboard can replace opening the broker's own app to check PNL - which, on Kite, would
/// invalidate this app's own access token via master-logout.
///
/// Implementations are expected to:
///  - return only genuinely open positions (net quantity != 0),
///  - scope the result to the exchanges this app actually trades (NFO, plus MCX when the
///    commodity add-on is enabled), so a stray position placed by hand in another segment doesn't
///    land in the dashboard's Total PNL with no context,
///  - fill in LotSize best-effort from the instrument master, and ExitPrice/ExitPnl via
///    <see cref="PositionExitPricing.Compute"/> so both brokers price exits identically.
/// </summary>
public interface IPositionsService
{
    Task<PositionsResult> GetPositionsAsync(CancellationToken cancellationToken);
}
