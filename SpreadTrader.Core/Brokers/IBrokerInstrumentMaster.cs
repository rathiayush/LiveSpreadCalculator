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
}
