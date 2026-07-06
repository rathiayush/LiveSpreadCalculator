namespace SpreadTrader.Core.Models;

/// <summary>
/// One open position from GET /portfolio/positions ("net" - the account's actual current
/// holding, not the "day" array Kite also returns, which resets every session and isn't what a
/// trader means by "my open position" mid-session). Quantity is signed: positive = long,
/// negative = short. Pnl is Kite's own "pnl" figure for this position (today's mark-to-market
/// plus realised - the same number Kite Web's Positions page shows).
/// See KitePositionsService / https://kite.trade/docs/connect/v3/portfolio/#retrieving-positions
/// </summary>
public sealed record PositionSnapshot(
    string TradingSymbol,
    string Exchange,
    string Product,
    int Quantity,
    decimal AveragePrice,
    decimal LastPrice,
    decimal Pnl,
    /// <summary>Exchange lot size, resolved best-effort from the instrument master (0 if it
    /// hasn't been loaded/resolved for this symbol yet - see KitePositionsService). Lets the
    /// dashboard offer "exit N lots" instead of forcing the user to type a raw share/contract
    /// quantity.</summary>
    int LotSize);

/// <summary>Result of KitePositionsService.GetPositionsAsync - mirrors AvailableMarginResult's
/// Success/ErrorMessage shape used elsewhere for Kite calls.</summary>
public sealed record PositionsResult(bool Success, IReadOnlyList<PositionSnapshot> Positions, string? ErrorMessage);
