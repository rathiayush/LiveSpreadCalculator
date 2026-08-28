namespace SpreadTrader.Core.Brokers;

/// <summary>
/// One tradeable contract as the engine sees it, normalized away from any single broker's
/// vocabulary. Both broker instrument masters project their own dump into this shape, which is
/// what lets the universe builder, positions filter and order dispatch stay broker-agnostic.
///
/// The normalization contract every implementation must honour:
///  - <paramref name="InstrumentToken"/> is the broker's own numeric handle for the contract
///    (Kite's <c>instrument_token</c>, Dhan's <c>securityId</c>). It is the key used by ITickStore
///    and by the market-data feed's subscribe/unsubscribe calls, so it must match whatever the
///    live feed reports back for the same contract.
///  - <paramref name="Exchange"/> is the app's canonical exchange code - "NFO" or "MCX" - NOT the
///    broker's segment string. Dhan's adapter maps NSE_FNO -> NFO and MCX_COMM -> MCX so that
///    every existing exchange comparison in the engine (see KitePositionsService's tracked-exchange
///    filter, McxOptions.Exchange, the universe CSVs) keeps working unchanged.
///  - <paramref name="Segment"/> carries the broker's own native segment string (Kite's "NFO-FUT",
///    Dhan's "NSE_FNO") for the adapter's own use - e.g. Dhan's order/subscription payloads need it.
///  - <paramref name="InstrumentType"/> is normalized to "FUT" for every futures contract,
///    whatever the broker calls it (Dhan uses FUTSTK/FUTIDX/FUTCOM).
/// </summary>
public sealed record BrokerInstrument(
    uint InstrumentToken,
    string TradingSymbol,
    string Name,
    string Exchange,
    string Segment,
    string InstrumentType,
    DateOnly? Expiry,
    int LotSize = 0,
    // Minimum price increment for this contract. Order prices must be an exact multiple of this
    // or the broker rejects the order outright. Mostly 0.05 for NSE F&O but genuinely varies by
    // instrument, hence tracked per-contract rather than assumed - see
    // OrderDispatchHostedService.ApplyMarketProtection.
    decimal TickSize = 0,
    // Option contracts only (InstrumentType "OPT") - the strike price and "CE"/"PE" right. Null
    // for every other instrument type (futures, equity, index). See
    // DhanInstrumentMasterService's option-chain parsing, the only current producer of these.
    decimal? Strike = null,
    string? OptionType = null);
