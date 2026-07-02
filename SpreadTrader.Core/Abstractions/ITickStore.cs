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
}
