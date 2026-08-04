namespace SpreadTrader.Core.Models;

/// <summary>
/// The latest computed value of a spread, pushed to the UI and evaluated against trigger rules.
/// Built from each leg's live market depth (never last traded price) - see
/// SpreadCalculationHostedService. Unlike a naive "always compare ask to ask" spread, the price
/// used for each leg depends on which side of that leg you'd actually be trading: the leg you'd
/// buy is priced at its best ASK (what you'd pay), and the leg you'd sell is priced at its best
/// BID (what you'd receive) - so SpreadValue reflects what the calendar-spread trade would
/// actually cost/pay right now, not a theoretical both-ask number. Direction is decided by
/// comparing the two legs' ask prices: if the near (current month) leg is pricier, you'd buy
/// near / sell far; if the far (next month) leg is pricier, you'd sell near / buy far - see
/// Direction and OrderDispatchHostedService.ResolveAutoDirection, which uses the same rule at
/// order-dispatch time. NearPrice/FarPrice are therefore each either that leg's ask or its bid,
/// depending on Direction - not always the same side. SpreadPercent expresses SpreadValue as a
/// percentage of NearPrice, since comparing raw price differences across underlyings at very
/// different price levels (e.g. NIFTY vs a small-cap future) isn't meaningful, but percentages
/// are - this is what the ranking sorts by.
///
/// SpreadValue/SpreadPercent/Direction only reflect whichever of the two directions the ask-ask
/// rule currently favors - fine for a rule that lets the engine auto-pick which leg is rich/cheap,
/// but wrong for a rule with FIXED leg actions that don't happen to match today's ask-ask
/// direction (the exact case an exit trigger is in: closing a position entered as, say, SellNear/
/// BuyFar always needs BuyNear/SellFar to close it, regardless of which side is currently richer).
/// BuySpreadValue/SellSpreadValue(/Percent) below solve that: both are ALWAYS computed, one per
/// fixed direction, so SpreadCalculationHostedService.EvaluateTriggersAsync can price each rule's
/// threshold off the specific side its own NearLegAction/FarLegAction actually trades - not
/// whichever side happens to be favored this tick.
/// </summary>
public readonly record struct SpreadSnapshot(
    string SpreadId,
    string Underlying,
    decimal NearPrice,
    decimal FarPrice,
    string Direction,
    decimal SpreadValue,
    decimal SpreadPercent,
    DateTimeOffset UpdatedAt,
    /// <summary>Always-computed BuyNear(ask)/SellFar(bid) price, regardless of which direction
    /// SpreadValue/Direction currently favors - shown alongside SellSpreadValue as a two-way live
    /// quote on the Live Spreads grid, AND used by EvaluateTriggersAsync to price any rule whose
    /// NearLegAction/FarLegAction is fixed to Buy/Sell. Same bid-missing fallback to that leg's
    /// own ask as the rest of this snapshot.</summary>
    decimal BuySpreadValue = 0m,
    /// <summary>Always-computed SellNear(bid)/BuyFar(ask) price - the other side of the two-way
    /// quote/fixed-direction pricing alongside BuySpreadValue.</summary>
    decimal SellSpreadValue = 0m,
    /// <summary>BuySpreadValue expressed as a percentage of the near leg's ask (the price actually
    /// paid in this direction) - the Buy-near/Sell-far counterpart to SpreadPercent, used for
    /// ThresholdIsPercent rules fixed to that direction.</summary>
    decimal BuySpreadPercent = 0m,
    /// <summary>SellSpreadValue expressed as a percentage of the near leg's bid (or ask, if bid
    /// hasn't arrived yet - the price actually received in this direction) - the Sell-near/Buy-far
    /// counterpart to SpreadPercent.</summary>
    decimal SellSpreadPercent = 0m,
    /// <summary>Live price of the UNDERLYING itself (the cash/index instrument this contract is
    /// written on), not of either futures leg - see SpotSymbology for how a futures underlying is
    /// matched to its spot instrument. 0 when no spot is available: the underlying's spot could not
    /// be resolved in the broker's master, or it resolved but no tick has arrived yet. The UI shows
    /// a dash rather than a zero in that case.</summary>
    decimal SpotPrice = 0m,
    /// <summary>The near future's premium (or discount) to spot, as a percentage of spot:
    /// (near - spot) / spot * 100. Positive means the future trades ABOVE cash (contango, the
    /// normal state given cost of carry); negative means below (backwardation). 0 whenever
    /// SpotPrice is 0, so an absent spot never reads as "trading exactly at cash".
    ///
    /// Deliberately independent of Direction: unlike SpreadValue this is a market observation
    /// rather than a price anything is executed at, so it does not switch sides with whichever
    /// leg is currently richer.</summary>
    decimal BasisPercent = 0m);

/// <summary>
/// A spread's snapshot with its rank (1 = biggest absolute spread %) among all currently
/// enabled spreads, as computed on the RankingIntervalMinutes timer (see
/// SpreadRankingHostedService). Pushed to the dashboard as "rankingUpdate" and also readable via
/// GET /api/spreads/ranking for clients that just connected.
/// </summary>
public readonly record struct SpreadRanking(
    int Rank,
    string SpreadId,
    string Underlying,
    decimal NearPrice,
    decimal FarPrice,
    string Direction,
    decimal SpreadValue,
    decimal SpreadPercent,
    DateTimeOffset UpdatedAt,
    /// <summary>Carried through from the snapshot this ranking row was built from, so the Dashboard
    /// can show spot/basis without subscribing to the full per-spread live feed - see
    /// SpreadSnapshot.SpotPrice/BasisPercent for the semantics (0 = unavailable).</summary>
    decimal SpotPrice = 0m,
    decimal BasisPercent = 0m);
