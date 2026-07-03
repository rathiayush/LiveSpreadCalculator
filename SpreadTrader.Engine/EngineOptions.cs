namespace SpreadTrader.Engine;

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>How often spreads are recalculated from the tick store and, if changed,
    /// pushed to the UI / evaluated against trigger rules. Lower = snappier UI, more CPU and
    /// more SignalR traffic. 300ms (the default in appsettings.json) is a reasonable default
    /// for ~200 instruments; this class-level default of 250 is only used if the Engine section
    /// is missing from configuration entirely.</summary>
    public int SpreadRecalculationIntervalMs { get; set; } = 250;

    /// <summary>Minimum time a re-armable (non one-shot) rule must wait between fires, even if
    /// the spread keeps crossing the threshold every recalculation tick. Guards against firing
    /// a burst of duplicate orders off a single noisy price move.</summary>
    public int MinFireIntervalPerRuleMs { get; set; } = 2000;

    /// <summary>How often (in minutes) the engine re-ranks every enabled spread by the absolute
    /// value of its spread percentage (biggest carry first) and pushes the ranked list to the
    /// dashboard as "rankingUpdate". Independent of SpreadRecalculationIntervalMs - this is a
    /// much slower, "what's most interesting right now" summary rather than a live tick feed.</summary>
    public int RankingIntervalMinutes { get; set; } = 3;

    /// <summary>Before punching a basket, fetch Kite's basket margin (with the hedge/spread
    /// benefit applied - see KiteMarginService.GetBasketMarginAsync) and the account's actual
    /// available margin, and refuse to place any leg at all if available margin is short. This
    /// is a fail-safe against ending up with a partially-filled, unhedged position purely
    /// because the account didn't have enough margin for the second leg - see
    /// OrderDispatchHostedService.DispatchAsync. Set to false to skip this pre-check and go
    /// straight to placing orders (Kite's own order entry still rejects genuinely
    /// insufficient-margin orders either way, so this only controls whether the *whole* basket
    /// is held back up front vs. discovered leg-by-leg).</summary>
    public bool VerifyMarginBeforeDispatch { get; set; } = true;

    /// <summary>Safety margin (in percent) required on top of the basket's final margin
    /// requirement before dispatch is allowed to proceed, e.g. 5 means available margin must be
    /// at least 105% of what Kite says the basket needs. Only used when
    /// VerifyMarginBeforeDispatch is true.</summary>
    public decimal MarginSafetyBufferPercent { get; set; } = 5m;

    /// <summary>If only one leg of a multi-leg basket fills (the other is rejected/fails), the
    /// account is left holding a naked, unhedged position instead of the intended calendar
    /// spread - which is both a completely different risk profile and loses the margin benefit
    /// the basket was placed for. When true, OrderDispatchHostedService immediately fires an
    /// opposite-side order on the filled leg to flatten it back out (also subject to
    /// MarketProtectionPercent below, same as any other MARKET-type leg).</summary>
    public bool AutoSquareOffOnPartialFill { get; set; } = true;

    /// <summary>Kite Connect's API rejects plain MARKET orders outright with "Market orders
    /// without market protection are not allowed via API" for F&amp;O - there's no
    /// market_protection flag in the order-placement API itself, so any leg configured as
    /// OrderType=MARKET (including the AutoSquareOffOnPartialFill cleanup order) is converted to
    /// a LIMIT order at a price this many percent through the best available quote instead: for
    /// a BUY, best-ask * (1 + this/100); for a SELL, best-bid * (1 - this/100) - see
    /// OrderDispatchHostedService.ApplyMarketProtection. This bounds worst-case slippage to the
    /// same band real "market protection" would, while still all-but-guaranteeing a fill.
    /// Falls back to the last traded price if no live bid/ask depth has arrived yet for that
    /// instrument. Set to 0 to send the limit price exactly at the best quote (no slippage
    /// headroom at all - more likely to sit unfilled if the market moves between decision and
    /// placement).</summary>
    public decimal MarketProtectionPercent { get; set; } = 3m;
}
