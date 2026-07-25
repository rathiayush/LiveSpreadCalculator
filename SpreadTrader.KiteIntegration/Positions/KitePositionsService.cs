using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration.Auth;
using SpreadTrader.KiteIntegration.Instruments;

namespace SpreadTrader.KiteIntegration.Positions;

/// <summary>
/// Calls Kite's GET /portfolio/positions to report live open positions and PNL - the same data
/// Kite Web/App's Positions page shows. This exists so the dashboard doesn't need the Kite app
/// open at all to check PNL: opening a fresh session there invalidates this app's own
/// access_token (see KiteAuthService's doc comment / the master-logout behavior documented at
/// https://kite.trade/docs/connect/v3/user/). Only the "net" side is surfaced - Kite also
/// returns a "day" array that resets every session, which isn't what a trader means by "my open
/// position" mid-session.
/// https://kite.trade/docs/connect/v3/portfolio/#retrieving-positions
/// </summary>
public sealed class KitePositionsService : IPositionsService
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly KiteAuthFailureHandler _authFailureHandler;
    private readonly InstrumentMasterService _instrumentMaster;
    private readonly ITickStore _tickStore;
    private readonly ILogger<KitePositionsService> _logger;
    private readonly HashSet<string> _trackedExchanges;

    public KitePositionsService(
        HttpClient httpClient,
        IOptions<KiteOptions> options,
        KiteSession session,
        KiteAuthFailureHandler authFailureHandler,
        InstrumentMasterService instrumentMaster,
        ITickStore tickStore,
        ILogger<KitePositionsService> logger,
        McxOptions? mcx = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _authFailureHandler = authFailureHandler;
        _instrumentMaster = instrumentMaster;
        _tickStore = tickStore;
        _logger = logger;
        // NFO is always tracked; MCX is added only when the commodity add-on is enabled. With the
        // flag off this set is exactly { "NFO" }, so the positions filter below behaves as before.
        _trackedExchanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NFO" };
        if (mcx is { Enabled: true })
            _trackedExchanges.Add(mcx.Exchange);
    }

    public async Task<PositionsResult> GetPositionsAsync(CancellationToken cancellationToken)
    {
        var accessToken = _session.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
            return new PositionsResult(false, Array.Empty<PositionSnapshot>(), "No Kite access token - complete the login flow first.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.RestBaseUrl}/portfolio/positions");
        request.Headers.Add("X-Kite-Version", "3");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_options.ApiKey}:{accessToken}");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : body;
                await _authFailureHandler.HandleIfTokenExceptionAsync(root, "fetching positions", cancellationToken);
                return new PositionsResult(false, Array.Empty<PositionSnapshot>(), message);
            }

            // Best-effort lot size lookup so the UI can offer "exit N lots" instead of a raw
            // quantity. Never blocks showing positions/PNL - if the instrument master hasn't
            // been fetched yet (see InstrumentsController/fetch-all), LotSize just comes back 0
            // per position and the UI falls back to raw quantity.
            try { await _instrumentMaster.EnsureLoadedAsync(cancellationToken); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not ensure instrument master is loaded while fetching positions - lot sizes may be unavailable.");
            }

            var net = root.GetProperty("data").GetProperty("net");
            var positions = new List<PositionSnapshot>();
            foreach (var p in net.EnumerateArray())
            {
                var quantity = p.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qv) ? qv : 0;
                if (quantity == 0) continue; // fully squared off already - nothing to show/act on

                var exchange = p.GetProperty("exchange").GetString() ?? "";
                // This app trades NFO (stock futures) and, when the MCX add-on is enabled, MCX
                // (commodity futures). Kite's positions response can also carry stray BSE/CDS/BFO
                // rows (e.g. a manual off-strategy trade placed via the Kite app itself). Excluding
                // those here keeps this page - and its Total PNL figure - scoped to what
                // SpreadTrader actually trades, instead of mixing in positions this dashboard has
                // no context for and can't meaningfully square off (lot sizes/contracts differ
                // entirely by segment). With the MCX flag off, _trackedExchanges is exactly
                // { "NFO" }, so this is identical to the original NFO-only filter.
                if (!_trackedExchanges.Contains(exchange)) continue;

                var tradingSymbol = p.GetProperty("tradingsymbol").GetString() ?? "";
                var product = p.TryGetProperty("product", out var pr) ? pr.GetString() ?? "" : "";
                var avgPrice = p.TryGetProperty("average_price", out var ap) && ap.TryGetDecimal(out var apv) ? apv : 0m;
                var lastPrice = p.TryGetProperty("last_price", out var lp) && lp.TryGetDecimal(out var lpv) ? lpv : 0m;
                var pnl = p.TryGetProperty("pnl", out var pn) && pn.TryGetDecimal(out var pnv) ? pnv : 0m;

                var instrument = _instrumentMaster.Find(tradingSymbol, exchange);
                var lotSize = instrument?.LotSize ?? 0;

                // Best-effort - see PositionExitPricing.Compute. Instrument token not resolved
                // yet, or no tick received for it yet, both just fall back to (0, 0) rather than
                // blocking the rest of this position's data from showing.
                Tick? tick = instrument is { } i && _tickStore.TryGet(i.InstrumentToken, out var t) ? t : null;
                var (exitPrice, exitPnl) = PositionExitPricing.Compute(quantity, avgPrice, tick);

                positions.Add(new PositionSnapshot(
                    tradingSymbol, exchange, product, quantity, avgPrice, lastPrice, pnl, lotSize, exitPrice, exitPnl,
                    // Kite's position rows carry no instrument token, so this is the instrument-master
                    // lookup already done above for lot size - reused rather than repeated downstream.
                    instrument?.InstrumentToken ?? 0));
            }

            return new PositionsResult(true, positions, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch positions");
            return new PositionsResult(false, Array.Empty<PositionSnapshot>(), ex.Message);
        }
    }
}
