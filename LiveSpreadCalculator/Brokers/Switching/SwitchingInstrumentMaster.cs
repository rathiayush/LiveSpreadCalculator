using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Instruments;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KotakIntegration.Instruments;

namespace LiveSpreadCalculator.Brokers.Switching;

/// <summary>
/// Forwards to whichever platform's instrument master is currently active. Each keeps its own
/// independent in-memory index and disk cache - switching platforms doesn't touch any of them, it
/// just changes which one this wrapper's calls land on. Every now-inactive platform's index simply
/// stops being read (and stops being refreshed by the once-daily job, which depends on this same
/// interface) until it becomes active again.
/// </summary>
public sealed class SwitchingInstrumentMaster : IBrokerInstrumentMaster
{
    private readonly BrokerSwitchState _state;
    private readonly InstrumentMasterService _kite;
    private readonly DhanInstrumentMasterService _dhan;
    private readonly KotakInstrumentMasterService _kotak;

    public SwitchingInstrumentMaster(
        BrokerSwitchState state, InstrumentMasterService kite, DhanInstrumentMasterService dhan, KotakInstrumentMasterService kotak)
    {
        _state = state;
        _kite = kite;
        _dhan = dhan;
        _kotak = kotak;
    }

    private IBrokerInstrumentMaster Active => _state.Current switch
    {
        BrokerPlatform.Kite => _kite,
        BrokerPlatform.Dhan => _dhan,
        _ => _kotak,
    };

    public Task EnsureLoadedAsync(CancellationToken cancellationToken) => Active.EnsureLoadedAsync(cancellationToken);

    public BrokerInstrument? Find(string tradingSymbol, string exchange) => Active.Find(tradingSymbol, exchange);

    public IReadOnlyList<BrokerInstrument> FindFutures(string underlyingName, string exchange = "NFO") =>
        Active.FindFutures(underlyingName, exchange);

    public IReadOnlyList<BrokerInstrument> GetAllFutures(string? exchange = null) => Active.GetAllFutures(exchange);

    public IReadOnlyList<string> GetAllFuturesUnderlyings(string exchange = "NFO") =>
        Active.GetAllFuturesUnderlyings(exchange);

    public Task<IReadOnlyList<BrokerInstrument>> FetchAndSaveFuturesAsync(string? exchange, CancellationToken cancellationToken) =>
        Active.FetchAndSaveFuturesAsync(exchange, cancellationToken);

    public Task ResolveAsync(InstrumentConfig config, CancellationToken cancellationToken) =>
        Active.ResolveAsync(config, cancellationToken);
}
