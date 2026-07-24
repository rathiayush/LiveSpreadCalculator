using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Converts a MARKET order leg into a protected IOC LIMIT order - Kite Connect's API rejects a
/// plain OrderType=MARKET leg for F&amp;O outright with "Market orders without market protection
/// are not allowed via API", and there's no market_protection parameter on the order-placement
/// endpoint itself to opt back in, so a bounded-slippage LIMIT order is the supported workaround:
/// priced <paramref name="slippagePercent"/> through a reference price (best-ask*(1+pct) for a
/// BUY, best-bid*(1-pct) for a SELL), aligned to the instrument's tick size, sent IOC so it fills
/// what it can immediately and cancels the rest rather than resting.
///
/// The slippage percent is supplied per call, not read from a single app-wide setting - triggered
/// baskets pass their own TriggerRule.SlippagePercent (user-chosen per rule, since a thinly-traded
/// far-month contract may need more headroom than a liquid near-month one - see TriggerRule's doc
/// comment), applied identically to whichever leg is being protected at the time, so both legs of
/// a fired rule get the same percentage band, each off its own reference price. Callers with no
/// rule to draw from (PositionsController's manual square-off) instead use whatever slippage % the
/// user entered on the square-off confirmation popup - see SquareOffPositionRequest.SlippagePercent.
///
/// Extracted as its own service (rather than living only on OrderDispatchHostedService, where
/// this logic originated) so every place in this app that fires a MARKET-type order - triggered
/// baskets, their partial-fill auto-square-off, and PositionsController's manual square-off from
/// the dashboard - sends something Kite will actually accept, using one copy of the band/
/// tick-rounding math instead of several that could quietly drift apart.
/// </summary>
public sealed class MarketProtectionService
{
    /// <summary>Fallback tick size used when an instrument's real one hasn't been resolved yet
    /// (universe/instrument master not built yet) - 0.05 is the tick size for the overwhelming
    /// majority of NSE F&amp;O contracts, so it's a far better guess than skipping alignment
    /// entirely.</summary>
    private const decimal DefaultTickSize = 0.05m;

    /// <summary>OrderLegRequest.PriceReference values. Kept as named constants (rather than
    /// free-floating string literals) so Protect/ProtectFromReference and every caller that reads
    /// them back (Telegram/dashboard formatting) can't drift out of sync on the exact text.
    /// ReferenceBidAsk is the only value any order-pricing path in this app produces today - a
    /// protected order is always anchored to live best bid/ask, never last traded price (LTP can
    /// be stale between prints - see Protect's doc comment). ReferenceLtp is kept only so a
    /// pre-existing triggers.csv/basket-history row written before this restriction still renders
    /// correctly; nothing produces it going forward.</summary>
    public const string ReferenceBidAsk = "Bid/Ask";
    public const string ReferenceLtp = "LTP";

    private readonly ITickStore _tickStore;
    private readonly ILogger<MarketProtectionService> _logger;

    public MarketProtectionService(ITickStore tickStore, ILogger<MarketProtectionService> logger)
    {
        _tickStore = tickStore;
        _logger = logger;
    }

    /// <summary>Tick-store-backed variant, for symbols that are part of the live-streamed
    /// universe (triggered baskets always are, since a spread can't exist without both legs
    /// subscribed). Looks up the latest bid/ask for <paramref name="instrumentToken"/> from the
    /// shared ITickStore and hands off to <see cref="ProtectFromReference"/> for the actual
    /// band/rounding math. Returns <paramref name="leg"/> unchanged - as a plain, unprotected
    /// MARKET order, which Kite will then likely reject outright rather than risk pricing it off
    /// a stale reference - if no tick data has arrived yet for this instrument, or if live bid/ask
    /// depth hasn't arrived for the side this leg needs (LTP is deliberately never used as a
    /// substitute here: it only updates on an actual trade and can be stale/off-market between
    /// prints, which is exactly the risk market protection exists to price around).</summary>
    public OrderLegRequest Protect(OrderLegRequest leg, uint instrumentToken, decimal tickSize, string context, decimal slippagePercent)
    {
        if (!string.Equals(leg.OrderType, "MARKET", StringComparison.OrdinalIgnoreCase))
            return leg;

        if (!_tickStore.TryGet(instrumentToken, out var tick))
        {
            _logger.LogWarning(
                "{Context}: no tick data yet for {Symbol} - sending as MARKET anyway, the broker will likely reject it (market protection requires a price reference).",
                context, leg.TradingSymbol);
            return leg;
        }

        var isBuy = string.Equals(leg.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase);
        var bidAskAvailable = isBuy ? tick.BestAskPrice > 0 : tick.BestBidPrice > 0;
        if (!bidAskAvailable)
        {
            _logger.LogWarning(
                "{Context}: no live bid/ask depth yet for {Symbol} - sending as MARKET anyway rather than pricing off LTP, the broker will likely reject it (market protection requires a real depth reference).",
                context, leg.TradingSymbol);
            return leg;
        }

        var reference = isBuy ? tick.BestAskPrice : tick.BestBidPrice;
        return ProtectFromReference(leg, reference, tickSize, context, slippagePercent, ReferenceBidAsk);
    }

    /// <summary>Reference-price variant - does the actual band/rounding math once a caller has
    /// already resolved a real bid/ask reference price (<see cref="Protect"/> is the only caller
    /// today, passing <see cref="ReferenceBidAsk"/> after confirming live depth exists). Not
    /// meant to be handed an LTP: this app never prices a protected order off last traded price -
    /// see <see cref="Protect"/>'s doc comment for why. <paramref name="referenceKind"/> has no
    /// default on purpose, so a caller can't silently fall into <see cref="ReferenceLtp"/>.
    ///
    /// Returns <paramref name="leg"/> unchanged if it isn't OrderType=MARKET, or if
    /// <paramref name="referencePrice"/> is not a usable (&gt; 0) price.</summary>
    public OrderLegRequest ProtectFromReference(OrderLegRequest leg, decimal referencePrice, decimal tickSize, string context, decimal slippagePercent, string referenceKind)
    {
        if (!string.Equals(leg.OrderType, "MARKET", StringComparison.OrdinalIgnoreCase))
            return leg;

        if (referencePrice <= 0)
        {
            _logger.LogWarning("{Context}: no usable price reference for {Symbol} yet - sending as MARKET anyway.", context, leg.TradingSymbol);
            return leg;
        }

        var isBuy = string.Equals(leg.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase);
        var band = slippagePercent / 100m;
        var rawPrice = isBuy ? referencePrice * (1 + band) : referencePrice * (1 - band);

        var effectiveTickSize = tickSize > 0 ? tickSize : DefaultTickSize;
        if (tickSize <= 0)
        {
            _logger.LogWarning(
                "{Context}: no tick size resolved for {Symbol} - falling back to {Default} (rebuild the universe/instrument master to pick up real tick sizes).",
                context, leg.TradingSymbol, DefaultTickSize);
        }

        var protectedPrice = RoundToTick(rawPrice, effectiveTickSize, roundUp: isBuy);

        _logger.LogInformation(
            "{Context}: {Symbol} MARKET -> protected LIMIT @ {Price} ({Percent}% band around {ReferenceKind} {Reference}, tick size {TickSize})",
            context, leg.TradingSymbol, protectedPrice, slippagePercent, referenceKind, referencePrice, effectiveTickSize);

        return new OrderLegRequest
        {
            TradingSymbol = leg.TradingSymbol,
            Exchange = leg.Exchange,
            TransactionType = leg.TransactionType,
            Quantity = leg.Quantity,
            Product = leg.Product,
            OrderType = "LIMIT",
            Price = protectedPrice,
            PriceReference = referenceKind,
            // IOC instead of the DAY default: fill whatever it can immediately and cancel the
            // rest right away, rather than resting in the book if price moves past the
            // protection band - see OrderDispatchHostedService's class doc comment for why this
            // matters for the auto-square-off/partial-fill logic that depends on a fast, known
            // terminal outcome.
            Validity = "IOC",
            Variety = leg.Variety,
            Tag = leg.Tag,
        };
    }

    /// <summary>Aligns a price to the nearest valid multiple of the instrument's tick size, in
    /// whichever direction keeps the order at least as permissive as the raw band-adjusted price
    /// - never more restrictive, since that could shave a fraction of a tick off the intended
    /// slippage protection and occasionally cost a fill for no real benefit. For a BUY, rounds up
    /// (willing to pay at least the intended protected price, never less); for a SELL, rounds
    /// down (willing to accept at least as low, never more). Kite LIMIT prices are always at most
    /// 2 decimal places regardless of tick size (0.05, 0.10, 1.00, ...), so the result is rounded
    /// to 2 decimals afterwards as a final safety net against any residual fractional-paisa
    /// noise from the multiplication/division.</summary>
    private static decimal RoundToTick(decimal price, decimal tickSize, bool roundUp)
    {
        if (tickSize <= 0) return Math.Round(price, 2);
        var ticks = price / tickSize;
        var roundedTicks = roundUp ? Math.Ceiling(ticks) : Math.Floor(ticks);
        return Math.Round(roundedTicks * tickSize, 2);
    }
}
