namespace SpreadTrader.Core.Models;

/// <summary>
/// One open position from GET /portfolio/positions ("net" - the account's actual current
/// holding, not the "day" array Kite also returns, which resets every session and isn't what a
/// trader means by "my open position" mid-session). Quantity is signed: positive = long,
/// negative = short. Pnl is Kite's own "pnl" figure for this position (today's mark-to-market
/// plus realised - the same number Kite Web's Positions page shows), tracked/projected using
/// LTP (see LivePositionsHostedService) - this is Kite's own definition of "PNL", left
/// untouched here.
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
    int LotSize,
    /// <summary>The price this position would ACTUALLY exit at right now if you squared it off
    /// this instant - a long position (Quantity &gt; 0, you bought it) exits by SELLING, so this
    /// is that instrument's live BID; a short position (Quantity &lt; 0, you sold it) exits by
    /// BUYING, so this is its live ASK. Deliberately different from LastPrice (LTP), which can
    /// sit on either side of the book or be stale between trades - this is the executable price,
    /// not just the last print. Falls back to LTP if the needed side of the book hasn't arrived
    /// yet (same convention as SpreadCalculationHostedService's bid-pending fallback). 0 if no
    /// tick has arrived for this instrument at all yet. See PositionExitPricing.Compute, shared
    /// by KitePositionsService (REST snapshot) and LivePositionsHostedService (live push) so both
    /// paths compute this identically.</summary>
    decimal ExitPrice,
    /// <summary>Quantity * (ExitPrice - AveragePrice) - what you'd actually walk away with if you
    /// squared this leg off this instant, priced off the real bid/ask you'd transact at rather
    /// than Kite's own LTP-anchored Pnl above. For a calendar spread's two legs, watching both
    /// legs' ExitPnl together (rather than only the combined Total PNL) is what tells you whether
    /// exiting NOW - buying back whichever leg you sold, selling whichever leg you bought - is
    /// actually worth it net of both legs' bid/ask spread, not just where LTP happens to sit.
    /// </summary>
    decimal ExitPnl);

/// <summary>Result of KitePositionsService.GetPositionsAsync - mirrors AvailableMarginResult's
/// Success/ErrorMessage shape used elsewhere for Kite calls.</summary>
public sealed record PositionsResult(bool Success, IReadOnlyList<PositionSnapshot> Positions, string? ErrorMessage);

/// <summary>
/// Shared "what would this leg actually exit at, right now" math - used identically by
/// KitePositionsService (building the plain REST snapshot) and LivePositionsHostedService
/// (streaming the live-projected one), so ExitPrice/ExitPnl never drift between the two paths.
/// See PositionSnapshot.ExitPrice/ExitPnl for the full reasoning.
/// </summary>
public static class PositionExitPricing
{
    public static (decimal ExitPrice, decimal ExitPnl) Compute(int quantity, decimal averagePrice, Tick? tick)
    {
        if (quantity == 0 || tick is not { } t) return (0m, 0m);

        // Long (bought) -> exit by SELLING -> priced at the BID (what you'd receive).
        // Short (sold) -> exit by BUYING -> priced at the ASK (what you'd pay).
        var exitPrice = quantity > 0
            ? (t.BestBidPrice > 0 ? t.BestBidPrice : t.LastPrice)
            : (t.BestAskPrice > 0 ? t.BestAskPrice : t.LastPrice);

        if (exitPrice == 0) return (0m, 0m);

        var exitPnl = quantity * (exitPrice - averagePrice);
        return (exitPrice, exitPnl);
    }
}
