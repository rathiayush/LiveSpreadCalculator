using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>Latest CDV snapshot per underlying, read by GET /api/cdv/snapshot so a dashboard
/// client that just connected (or missed a SignalR push) isn't empty until the next bar. The CDV
/// counterpart of ISpreadSnapshotCache.</summary>
public interface ICdvSnapshotCache
{
    void Upsert(CdvSnapshot snapshot);
    IReadOnlyList<CdvSnapshot> Snapshot();
}
