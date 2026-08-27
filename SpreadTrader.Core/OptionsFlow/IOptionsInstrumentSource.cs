namespace SpreadTrader.Core.OptionsFlow;

/// <summary>
/// Resolves index option contracts (CE/PE across strikes) for the Options Flow add-on.
///
/// This is a SEPARATE interface from IBrokerInstrumentMaster on purpose. That one is the futures
/// universe the trading engine resolves spread legs and orders against, and both its
/// implementations deliberately discard every non-futures row as they parse, to keep a few hundred
/// contracts in memory instead of the tens of thousands an index option chain adds. Widening it to
/// carry options would change the memory profile and the meaning of every existing lookup on a hot
/// trading path, to serve a read-only chart. So the option chain is loaded and held apart, only
/// when the feature flag is on, and nothing in the trading path can see it.
///
/// Implementations only need the small slice this add-on uses: the nearest-expiry chain for a
/// named index.
/// </summary>
public interface IOptionsInstrumentSource
{
    /// <summary>False when the active broker cannot supply an option chain, so the engine can
    /// report a clear reason instead of an empty chart.</summary>
    bool IsAvailable { get; }

    /// <summary>Loads (or refreshes) the option chain. Safe to call repeatedly; a no-op once
    /// loaded and fresh.</summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken);

    /// <summary>The nearest expiry on or after today for this index, or null when the chain has no
    /// contracts for it.</summary>
    DateOnly? GetNearestExpiry(string underlying);

    /// <summary>Every contract for one index and expiry, both rights, all strikes. The engine
    /// picks its own ATM-centred window out of this - see OptionsFlowEngine.</summary>
    IReadOnlyList<OptionContract> GetChain(string underlying, DateOnly expiry);
}
