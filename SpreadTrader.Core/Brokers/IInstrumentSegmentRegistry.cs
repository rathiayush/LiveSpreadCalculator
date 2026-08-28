namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Lets a caller teach the live feed which broker-native exchange segment an instrument token
/// belongs to, for a token that isn't covered by IBrokerInstrumentMaster's futures-only lookups
/// (see that interface's doc comment) - e.g. an open position, or (see CdvHostedService) an option
/// contract. Dhan's WebSocket subscribe payload needs an (exchangeSegment, securityId) pair per
/// token, not just the numeric id, so a token the feed client cannot place a segment for is
/// silently dropped rather than subscribed - see DhanMarketFeedClient.ResolveSubscriptionEntries.
///
/// Currently implemented only by DhanSegmentRegistry. Kept as its own interface, in Core like every
/// other broker abstraction, for the same reason as IOptionChainSource: it lets SpreadTrader.Engine
/// depend on it without referencing a broker project directly.
/// </summary>
public interface IInstrumentSegmentRegistry
{
    /// <summary>Records the segment a token belongs to. Safe to call redundantly (e.g. for a token
    /// IBrokerInstrumentMaster already knows) - implementations only consult this as a fallback.</summary>
    void Remember(uint instrumentToken, string? brokerSegment);
}
