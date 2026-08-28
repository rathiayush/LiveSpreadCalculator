using Microsoft.AspNetCore.SignalR;
using LiveSpreadCalculator.Hubs;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Realtime;

/// <summary>
/// The CDV counterpart of SignalRSpreadPublisher: broadcasts each bar's CdvSnapshot as "cdvUpdate"
/// on the same SpreadHub every connected dashboard client already listens on. Also upserts into
/// ICdvSnapshotCache here (rather than in CdvHostedService, unlike SpreadCalculationHostedService's
/// split of that responsibility) - CDV only ever calls PublishAsync, so folding the cache write in
/// here keeps CdvHostedService from needing a second injected dependency for what is, for this
/// feature, always the same "publish" moment.
/// </summary>
public sealed class SignalRCdvPublisher : ICdvPublisher
{
    private readonly IHubContext<SpreadHub> _hub;
    private readonly ICdvSnapshotCache _cache;

    public SignalRCdvPublisher(IHubContext<SpreadHub> hub, ICdvSnapshotCache cache)
    {
        _hub = hub;
        _cache = cache;
    }

    public Task PublishAsync(CdvSnapshot snapshot, CancellationToken cancellationToken)
    {
        _cache.Upsert(snapshot);
        return _hub.Clients.All.SendAsync("cdvUpdate", snapshot, cancellationToken);
    }
}
