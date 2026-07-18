using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// The in-memory, process-local store of the latest tick per instrument. No history, no
/// persistence - by design, per the "no data storage" requirement. A restart loses all state.
/// </summary>
public interface ITickStore
{
    void Update(Tick tick);
    bool TryGet(uint instrumentToken, out Tick tick);
    IReadOnlyCollection<Tick> Snapshot();

    /// <summary>Drops a stale entry once nothing needs it any more - see
    /// TickIngestionHostedService's subscription-reconciliation loop, which is the only caller.
    /// Without this, every instrument token this process ever subscribed to over its lifetime
    /// (across spread edits, deleted spreads, and closed positions - none of which used to prune
    /// anything) stayed in here forever.</summary>
    void Remove(uint instrumentToken);
}
