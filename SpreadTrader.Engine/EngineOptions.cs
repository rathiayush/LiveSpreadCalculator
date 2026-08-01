namespace SpreadTrader.Engine;

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>How often spreads are recalculated from the tick store and, if changed,
    /// pushed to the UI / evaluated against trigger rules. Lower = snappier UI, more CPU and
    /// more SignalR traffic. 150ms (the value in appsettings.json) roughly doubles the recalc/
    /// evaluation rate versus the previous 300ms default - still cheap enough at ~100-200
    /// instruments / ~100 spreads (see the class doc comment on SpreadCalculationHostedService).
    /// This class-level default of 250 is only used if the Engine section is missing from
    /// configuration entirely.</summary>
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
    /// benefit applied - see IMarginService.GetBasketMarginAsync) and the account's actual
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

    /// <summary>OPT-IN FALLBACK, OFF BY DEFAULT. The default policy is ALWAYS-FILL: an incomplete
    /// basket is driven to completion by RetryUnfilledLegOnPartialFill below, never by unwinding
    /// the side that already filled - giving a filled leg back throws away the position the rule
    /// fired for and pays the bid/ask spread twice to end up flat. Set this to true only if you
    /// would genuinely rather be flat than exposed when a leg cannot be filled: it makes
    /// OrderDispatchHostedService fire an opposite-side order for whatever quantity is left
    /// unmatched (also subject to MarketProtectionPercent, same as any other MARKET-type leg).
    ///
    /// With this off, a leg the retries could NOT fill is left standing as a real unhedged
    /// position - logged at Critical (see OrderDispatchHostedService.WarnIfLegsLeftUnmatched)
    /// rather than silently, but still live and needing manual attention.</summary>
    public bool AutoSquareOffOnPartialFill { get; set; } = false;

    /// <summary>The always-fill mechanism, and the main thing keeping a basket from ending up
    /// lopsided. Re-attempts whichever leg(s) still have quantity outstanding at
    /// RetryUnfilledLegSlippageMultiplier times the rule's own SlippagePercent, each attempt
    /// re-priced off the current market - see OrderDispatchHostedService.RetryUnfilledLegsAsync.
    /// Hedge (BUY) legs are chased to the rule's full requested quantity; short (SELL) legs are
    /// capped at whatever the hedge currently holds, so the position stays hedged at every
    /// intermediate step while it fills in. Turning this off means an incomplete basket simply
    /// stays incomplete.</summary>
    public bool RetryUnfilledLegOnPartialFill { get; set; } = true;

    /// <summary>How much wider than the rule's own SlippagePercent every retry above is allowed
    /// to price through - e.g. 3 means a rule fired with 0.10% slippage retries its rejected leg
    /// at 0.30%. The SAME multiplier is reused for every attempt (see RetryUnfilledLegMaxAttempts)
    /// rather than widening further each round - only the reference price is refreshed each
    /// attempt, not the band. Only used when RetryUnfilledLegOnPartialFill is true.</summary>
    public decimal RetryUnfilledLegSlippageMultiplier { get; set; } = 3m;

    /// <summary>How many rounds OrderDispatchHostedService.RetryUnfilledLegsAsync runs to fill an
    /// incomplete basket, each at RetryUnfilledLegSlippageMultiplier's wider slippage band and each
    /// re-priced off the latest market price. Only used when RetryUnfilledLegOnPartialFill is true.
    ///
    /// Needs headroom because a round only advances one side at a time: the hedge is topped up
    /// first, and only the NEXT round can lift the short against the hedge that round just added
    /// (see ComputeLegTargets). So a basket that needs both sides topped up costs two rounds
    /// minimum - 4 leaves room for a failed round on either side rather than stopping half-way with
    /// a leg still outstanding.</summary>
    public int RetryUnfilledLegMaxAttempts { get; set; } = 4;

    /// <summary>Cap on consecutive fires of the same rule that fill nothing at all (blocked on
    /// margin, or every leg rejected/skipped - see OrderDispatchHostedService.
    /// RestoreRuleIfNothingFilled) before the rule is auto-disarmed instead of being given its
    /// shot back again. Without this, a condition that can never actually fill - chronic
    /// insufficient margin, a frozen/delisted instrument, a persistently rejected order - would
    /// re-arm and re-fire forever every time the spread re-crosses the threshold. 10 consecutive
    /// misses and it stops, waiting for a human to look at it and re-enable manually (see
    /// TriggerRuleControllerBase.Enable). TriggerRule.UnfilledAttempts tracks the running count
    /// and resets to 0 the moment any fire actually fills something.</summary>
    public int MaxUnfilledDispatchAttempts { get; set; } = 10;

    /// <summary>Kite Connect's API rejects plain MARKET orders outright with "Market orders
    /// without market protection are not allowed via API" for F&amp;O - there's no
    /// market_protection flag in the order-placement API itself, so any leg configured as
    /// OrderType=MARKET is converted to a LIMIT order at a price some percent through the best
    /// available quote instead: for a BUY, best-ask * (1 + pct/100); for a SELL,
    /// best-bid * (1 - pct/100) - see MarketProtectionService.
    ///
    /// Trigger-fired baskets (and their partial-fill auto-square-off cleanup) use the firing
    /// TriggerRule's own SlippagePercent instead of this value, since the right amount of
    /// headroom varies contract to contract - see TriggerRule.SlippagePercent, chosen per rule
    /// in the trigger-creation UI. This setting now only supplies the band for
    /// PositionsController's manual square-off, where there's no TriggerRule to draw a
    /// per-order value from.
    ///
    /// Falls back to the last traded price if no live bid/ask depth has arrived yet for that
    /// instrument. Set to 0 to send the limit price exactly at the best quote (no slippage
    /// headroom at all - more likely to sit unfilled if the market moves between decision and
    /// placement).</summary>
    public decimal MarketProtectionPercent { get; set; } = 3m;

    // ---- Hourly spread history capture (see SpreadHistoryCaptureHostedService) ------------------

    /// <summary>Designates THIS instance as the single writer of the shared spread-history database.
    /// Exactly one deployed instance (pradeep-2) should set this true; it alone captures snapshots,
    /// creates the schema, and runs the expiry/retention purges. Every other instance leaves this
    /// false and only READS the history (serving the chart) - no captures, no schema init, no
    /// deletes - so multiple instances pointed at one database never double-write or race on it.</summary>
    public bool SpreadHistoryWriter { get; set; } = false;

    /// <summary>Master switch for the hourly spread-history capture job. When false the background
    /// service does nothing (no snapshots written, no DB touched beyond startup schema init). Only
    /// consulted on the writer instance (SpreadHistoryWriter=true); readers never capture regardless.</summary>
    public bool SpreadHistoryCaptureEnabled { get; set; } = true;

    /// <summary>The clock times (IST, "HH:mm") at which a snapshot of every spread is captured and
    /// bulk-inserted into the history database on each trading day. Default is the :29 mark of each
    /// trading hour from just after the 09:15 open through the 15:29 pre-close (7 captures/day).
    /// Add "top of hour" entries (e.g. "10:00") here if you also want those. Times are matched to
    /// the minute in IST regardless of the server's own timezone.</summary>
    public string[] SpreadHistorySnapshotTimesIst { get; set; } =
        { "09:29", "10:29", "11:29", "12:29", "13:29", "14:29", "15:29" };

    /// <summary>Capture only on weekdays (Mon-Fri). NSE is closed on weekends; leave true unless you
    /// want rows on Saturday/Sunday too (this does NOT account for exchange holidays - a snapshot on
    /// a holiday simply records whatever the last live values were, or nothing if none are cached).</summary>
    public bool SpreadHistoryWeekdaysOnly { get; set; } = true;

    /// <summary>When true, once per day (on the first capture of the day) the engine deletes history
    /// rows whose near leg has already expired - the monthly "when a leg expires, flush that leg's
    /// data and move to the next legs" behaviour (see ISpreadHistoryStore.PurgeExpiredAsync).</summary>
    public bool SpreadHistoryPurgeExpiredLegs { get; set; } = true;

    // ---- Daily maintenance job (see DailyMaintenanceHostedService) ------------------------------

    /// <summary>Master switch for the once-a-day maintenance job that clears trigger data and trims
    /// old spread history. When false the job does nothing.</summary>
    public bool DailyMaintenanceEnabled { get; set; } = true;

    /// <summary>IST clock time ("HH:mm") the daily maintenance job runs at. Default 06:00 - well
    /// before the 09:15 market open, so the day starts from a clean trigger slate.</summary>
    public string DailyMaintenanceTimeIst { get; set; } = "06:00";

    /// <summary>Run the maintenance job on weekdays (Mon-Fri) only. Default true - the market is
    /// closed on Saturday/Sunday, so there's no reason to clear triggers/activity or purge history
    /// then. Set false to run it every day.</summary>
    public bool DailyMaintenanceWeekdaysOnly { get; set; } = true;

    /// <summary>IST clock time ("HH:mm") InstrumentMasterRefreshHostedService re-downloads the
    /// broker's instrument/scrip master. Default 06:00 - same slot as daily maintenance, well
    /// before the 09:15 open, so the day's trading starts against the current month's contract
    /// list rather than whatever was cached at the last restart. Runs every day, including
    /// weekends - there's no trading-state reason to skip them here, and it means Monday's open
    /// isn't the first time the week's data gets refreshed.</summary>
    public string InstrumentMasterRefreshTimeIst { get; set; } = "06:00";

    /// <summary>Daily job wipes ALL entry trigger rules (ITriggerRuleStore.RemoveAll).</summary>
    public bool DailyMaintenanceClearTriggers { get; set; } = true;

    /// <summary>Daily job wipes ALL exit trigger rules (IExitTriggerRuleStore.RemoveAll).</summary>
    public bool DailyMaintenanceClearExitTriggers { get; set; } = true;

    /// <summary>Daily job wipes the in-memory trigger activity feed (ITriggerActivityStore.Clear).</summary>
    public bool DailyMaintenanceClearTriggerActivity { get; set; } = true;

    /// <summary>Daily job deletes spread history rows older than this many days
    /// (ISpreadHistoryStore.PurgeOlderThanAsync) - an age-based retention cap independent of the
    /// per-leg expiry flush. Set to 0 or less to skip age-based trimming.</summary>
    public int SpreadHistoryRetentionDays { get; set; } = 30;

    // ---- Daily spread threshold alert (see SpreadCalculationHostedService.CheckDailySpreadAlertsAsync) ----

    /// <summary>Master switch for the once-a-day Telegram alert listing every spread whose
    /// |spread %| has reached a configured tier (see DailySpreadAlertThresholdsPercent). Piggybacks
    /// on the same recalculation cycle that already computes spread % for every spread every
    /// SpreadRecalculationIntervalMs - deliberately not a separate hosted service or CSV file,
    /// since there's nothing this needs that isn't already computed/held in memory there every
    /// cycle.</summary>
    public bool DailySpreadAlertEnabled { get; set; } = true;

    /// <summary>Each value is an independent tier: the first time, on a given IST calendar day,
    /// that any spread's |spread %| reaches or exceeds this number, one Telegram message listing
    /// every spread currently past it (with its signed spread %) is sent - then that tier goes
    /// quiet for the rest of the day, even if more spreads cross it or the same ones cross it
    /// again. A spread past the higher tier is also past the lower one, so it can appear in both
    /// tiers' messages the same day; that's intentional - they're independent notifications, not
    /// mutually exclusive buckets.</summary>
    public List<decimal> DailySpreadAlertThresholdsPercent { get; set; } = new() { 2.5m, 3.0m };

    /// <summary>IST clock time ("HH:mm") before which the daily spread alert never fires, even if
    /// a tier is technically crossed - spread % right at/after the 09:15 open can be noisy before
    /// the market settles into real liquidity, and this is a way to skip that window rather than
    /// alert on it. Default 09:25, ten minutes after open.</summary>
    public string DailySpreadAlertStartTimeIst { get; set; } = "09:25";
}
