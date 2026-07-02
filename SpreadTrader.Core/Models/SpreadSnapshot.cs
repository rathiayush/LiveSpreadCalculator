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
/// </summary>
public readonly record struct SpreadSnapshot(
    string SpreadId,
    string Underlying,
    decimal NearPrice,
    decimal FarPrice,
    string Direction,
    decimal SpreadValue,
    decimal SpreadPercent,
    DateTimeOffset UpdatedAt);

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
    DateTimeOffset UpdatedAt);
