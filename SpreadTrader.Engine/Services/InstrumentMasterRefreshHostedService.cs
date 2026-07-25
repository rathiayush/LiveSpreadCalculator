using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Keeps the broker instrument master warm by calling EnsureLoadedAsync on a fixed schedule, so no
/// live HTTP request is ever the one that pays for a stale-cache reload.
///
/// WHY THIS EXISTS. Both instrument masters (Dhan's DhanInstrumentMasterService, Kite's
/// InstrumentMasterService) cache their scrip dump for 12 hours and reload lazily - the FIRST
/// caller after that window expires pays the full download+parse cost inline. On Dhan that dump is
/// one flat ~26 MB file covering every exchange and instrument type (there's no exchange-scoped
/// endpoint - see DhanInstrumentMasterService's doc comment), and several live, frequently-hit
/// endpoints call EnsureLoadedAsync directly in their request path: GET /api/instruments/futures*
/// (the Add Spread underlying/expiry pickers), GET /api/positions, and POST /api/spreads. Whichever
/// request happens to land the moment the cache goes stale eats a multi-second download it did
/// nothing to deserve.
///
/// This runs the same EnsureLoadedAsync call on its own timer, well inside the 12-hour cache
/// window, so IsFresh() is true by the time any real request arrives and that request's own call
/// is a same-thread no-op (an in-memory timestamp check, no I/O, no lock contention worth
/// mentioning). The lazy on-demand path in each instrument master is UNCHANGED and stays as the
/// safety net for the gap between process startup and this loop's first tick, and for the case
/// where a refresh has been failing repeatedly (see DhanInstrumentMasterService.EnsureLoadedAsync's
/// own doc comment on that - it never blanks out already-loaded data on a failed refresh).
///
/// Broker-agnostic on purpose: it depends on IBrokerInstrumentMaster only, so it works unchanged
/// whichever broker is configured, and Kite's implementation - which has the identical 12-hour lazy
/// cache shape, just against a much smaller per-exchange file - gets the same protection for free.
/// </summary>
public sealed class InstrumentMasterRefreshHostedService : BackgroundService
{
    // Comfortably inside the 12-hour cache window each instrument master already uses, so a
    // refresh always lands well before any lazy-reload fallback would trigger. On Dhan this is
    // cheap even when the file HAS changed (the download runs off the request thread entirely),
    // and near-free when it hasn't (DhanInstrumentMasterService's conditional GET turns a no-op
    // refresh into one small request instead of re-downloading 26 MB) - so refreshing this often
    // costs nothing that matters and buys faster convergence on new listings/lot-size changes.
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(3);

    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly ILogger<InstrumentMasterRefreshHostedService> _logger;
    private readonly TimeSpan _refreshInterval;

    public InstrumentMasterRefreshHostedService(
        IBrokerInstrumentMaster instrumentMaster,
        ILogger<InstrumentMasterRefreshHostedService> logger)
        : this(instrumentMaster, logger, DefaultRefreshInterval)
    {
    }

    /// <summary>Overload taking an explicit interval - not selected by DI (nothing registers a
    /// bare TimeSpan, so the container falls back to the two-argument constructor automatically),
    /// used to drive this service on a short interval in tests without waiting on the real
    /// 3-hour cadence.</summary>
    public InstrumentMasterRefreshHostedService(
        IBrokerInstrumentMaster instrumentMaster,
        ILogger<InstrumentMasterRefreshHostedService> logger,
        TimeSpan refreshInterval)
    {
        _instrumentMaster = instrumentMaster;
        _logger = logger;
        _refreshInterval = refreshInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A no-op when the cache is still fresh (the common case, since this fires well
                // inside the 12h window) - only actually hits the network when due.
                await _instrumentMaster.EnsureLoadedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never fatal: whatever was last successfully loaded stays in place (both
                // implementations only swap in new data after a successful parse), so a failed
                // background refresh costs nothing beyond staying on slightly older data until the
                // next tick - not a reason to take the process down.
                _logger.LogWarning(ex, "Background instrument master refresh failed - keeping the previously loaded data, will retry next cycle.");
            }

            if (!await DelayAsync(_refreshInterval, stoppingToken)) return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
