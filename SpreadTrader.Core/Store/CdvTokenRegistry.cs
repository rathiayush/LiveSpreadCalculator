using System.Collections.Concurrent;

namespace SpreadTrader.Core.Store;

/// <summary>
/// The set of instrument tokens CdvHostedService currently needs live ticks for (the resolved
/// ATM-strike CE/PE chain for each configured underlying). Exists purely so
/// TickIngestionHostedService.ReconcileSubscriptionsAsync - which recomputes "what does this
/// process actually need" from spread legs and open positions ONLY, then unsubscribes and drops
/// from ITickStore anything else the ticker is subscribed to - doesn't treat CDV's own
/// subscriptions as stale and silently unsubscribe them within one reconcile cycle (see that
/// method's doc comment). SpreadTrader.Engine has no reference from TickIngestionHostedService to
/// anything CDV-specific; this small registry is the entire coupling between the two.
///
/// Rebuilt wholesale by CdvHostedService each time it (re)resolves a strike chain (new trading day,
/// or a chain added/changed), not incrementally added/removed token by token - same "recompute from
/// scratch" reasoning ReconcileSubscriptionsAsync itself already uses.
/// </summary>
public sealed class CdvTokenRegistry
{
    private volatile IReadOnlyCollection<uint> _tokens = Array.Empty<uint>();

    public void SetTokens(IReadOnlyCollection<uint> tokens) => _tokens = tokens;

    public IReadOnlyCollection<uint> GetTokens() => _tokens;
}
