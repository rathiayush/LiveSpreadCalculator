namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Index-option-chain lookups for the CDV feature (see CdvHostedService) - deliberately NOT part
/// of IBrokerInstrumentMaster, which is futures-only by design (see that interface's doc comment).
/// Kept as its own small interface, in Core like every other broker abstraction, specifically so
/// SpreadTrader.Engine can depend on it without referencing a broker project directly - the same
/// boundary IBrokerInstrumentMaster/IMarketDataFeed/etc. already preserve (see
/// BrokerRegistration's doc comment: "SpreadTrader.Engine still never references any broker
/// project directly").
///
/// Currently implemented only by DhanInstrumentMasterService - the CDV feature is Dhan-only (see
/// CdvOptions), so this is registered once, always pointing at Dhan's own scrip-master index,
/// regardless of which platform BrokerSwitchState currently has active for trading. That also
/// means CDV only produces meaningful data while Dhan is the active platform: the option/spot
/// tokens this resolves are Dhan securityIds, and IMarketDataFeed (the broker-switching feed
/// CdvHostedService subscribes them against) only forwards to Dhan's socket while Dhan is active.
/// </summary>
public interface IOptionChainSource
{
    /// <summary>Downloads/refreshes the underlying instrument dump if needed - same contract as
    /// IBrokerInstrumentMaster.EnsureLoadedAsync (in Dhan's case, it's the very same dump).</summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken);

    /// <summary>The cash/index instrument for this underlying's live spot price - see
    /// IBrokerInstrumentMaster.FindSpot for the exact matching rules.</summary>
    BrokerInstrument? FindSpot(string underlyingName);

    /// <summary>Every option expiry currently listed for this underlying, ascending.</summary>
    IReadOnlyList<DateOnly> GetOptionExpiries(string underlying);

    /// <summary>The nearest listed expiry that hasn't passed <paramref name="asOf"/>, or null if
    /// none is listed - CDV only ever subscribes this one, never the full chain.</summary>
    DateOnly? GetNearestOptionExpiry(string underlying, DateOnly asOf);

    /// <summary>Resolves one CE/PE contract, or null if that strike isn't listed for this expiry.</summary>
    BrokerInstrument? FindOption(string underlying, DateOnly expiry, decimal strike, bool isCall);
}
