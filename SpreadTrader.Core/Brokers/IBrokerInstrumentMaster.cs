using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Downloads and caches the broker's instrument dump so a tradingsymbol can be resolved to the
/// numeric token the live feed and order API need, and so the universe builder can enumerate every
/// futures contract currently listed.
///
/// Both implementations narrow their broker's dump to FUTURES ONLY, on the configured exchanges,
/// as they parse - a full dump is 100k+ rows across every option strike and expiry, and this app
/// only ever trades futures calendar spreads. See each implementation's doc comment for how it
/// does that, and BrokerInstrument for the normalization contract they both honour.
/// </summary>
public interface IBrokerInstrumentMaster
{
    /// <summary>Downloads the dump if it isn't cached, or if the cache has aged out. Safe to call
    /// concurrently and on every request - it's a no-op once loaded.</summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken);

    /// <summary>Exact contract lookup by tradingsymbol + canonical exchange ("NFO"/"MCX").</summary>
    BrokerInstrument? Find(string tradingSymbol, string exchange);

    /// <summary>Every futures contract for one underlying, ordered by expiry ascending - so index
    /// 0 is the near month and index 1 the far month.</summary>
    IReadOnlyList<BrokerInstrument> FindFutures(string underlyingName, string exchange = "NFO");

    /// <summary>Every cached futures contract, optionally narrowed to one exchange.</summary>
    IReadOnlyList<BrokerInstrument> GetAllFutures(string? exchange = null);

    /// <summary>Every distinct underlying with at least one listed futures contract on the given
    /// exchange - the "fetch everything available" symbol source for the universe builder.</summary>
    IReadOnlyList<string> GetAllFuturesUnderlyings(string exchange = "NFO");

    /// <summary>Refreshes the dump if needed and writes the futures list to the configured CSV so
    /// the symbol picker has something on disk without hitting the broker again.</summary>
    Task<IReadOnlyList<BrokerInstrument>> FetchAndSaveFuturesAsync(string? exchange, CancellationToken cancellationToken);

    /// <summary>Fills in InstrumentToken, Expiry, Underlying, LotSize and TickSize on a spread leg
    /// from the cached dump.</summary>
    /// <exception cref="InvalidOperationException">The symbol isn't in the broker's master.</exception>
    Task ResolveAsync(InstrumentConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// The CASH/INDEX instrument a futures underlying is written on (RELIANCE futures -&gt; the
    /// RELIANCE equity; NIFTY futures -&gt; the NIFTY 50 index), so the live spot price can be shown
    /// next to the futures legs - see SpreadSnapshot.SpotPrice/BasisPercent.
    ///
    /// Kept separate from the futures lookups above on purpose: those are futures-only by design
    /// (see the interface doc comment) and the universe builder depends on that, so implementations
    /// hold spot instruments in their own list rather than mixing them into the futures one.
    ///
    /// Returns null when no spot exists or none could be matched - an unlisted underlying, an index
    /// whose name isn't in SpotSymbology, or an MCX contract, which has no cash equivalent.
    /// Callers must treat null as "show no spot", never as an error: see SpotSymbology on why
    /// failing to resolve is much safer than matching the wrong instrument.
    /// </summary>
    BrokerInstrument? FindSpot(string underlyingName);

    /// <summary>
    /// Every cached cash/index instrument. Deliberately NOT merged into GetAllFutures, which stays
    /// futures-only.
    ///
    /// Exists for the feed clients: on Dhan and Kotak a token is only meaningful together with its
    /// segment, and both build their subscribe payload's token-&gt;segment map from GetAllFutures -
    /// so without this a spot token would be silently dropped as "unknown segment" and its price
    /// would never arrive. Kite needs none of this (its instrument_token is globally unique) but
    /// implements it for symmetry.
    /// </summary>
    IReadOnlyList<BrokerInstrument> GetAllSpot();
}
