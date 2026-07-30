using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KotakIntegration.Positions;

/// <summary>
/// Calls Kotak Neo's GET quick/user/positions to report live open positions and PNL - the Kotak
/// counterpart of KitePositionsService/DhanPositionsService.
///
/// Like Dhan, this app has no confirmed evidence Kotak's positions response carries a
/// last-traded-price field, so LastPrice is taken from the live tick store the same way
/// DhanPositionsService does (see its doc comment) - PositionsCache anchors a subscription for
/// every open position specifically so that data is there.
///
/// NO MCX. Only NFO is ever tracked here, regardless of McxOptions - see KotakSymbology's class doc
/// comment for why: Kotak Neo's documented exchange-segment list has no MCX entry at all, so an MCX
/// position could never resolve to a valid Kotak segment even if one existed on the account.
///
/// RESPONSE FIELD NAMES ARE BEST-EFFORT - see KotakApiClient's doc comment for the overall caveat
/// on this integration's confidence level pending a real account to test against.
/// </summary>
public sealed class KotakPositionsService : IPositionsService
{
    private readonly KotakApiClient _api;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly ITickStore _tickStore;
    private readonly ILogger<KotakPositionsService> _logger;

    public KotakPositionsService(
        KotakApiClient api,
        IBrokerInstrumentMaster instrumentMaster,
        ITickStore tickStore,
        ILogger<KotakPositionsService> logger)
    {
        _api = api;
        _instrumentMaster = instrumentMaster;
        _tickStore = tickStore;
        _logger = logger;
    }

    public async Task<PositionsResult> GetPositionsAsync(CancellationToken cancellationToken)
    {
        if (!_api.HasSession)
            return new PositionsResult(false, Array.Empty<PositionSnapshot>(), "No Kotak session - complete the login flow first.");

        using var response = await _api.SendSessionAsync(
            HttpMethod.Get, "quick/user/positions", null, "fetching positions", cancellationToken);

        if (!response.Success)
            return new PositionsResult(false, Array.Empty<PositionSnapshot>(), response.ErrorMessage);

        try { await _instrumentMaster.EnsureLoadedAsync(cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure instrument master is loaded while fetching positions - lot sizes may be unavailable.");
        }

        var root = response.Data;
        if (root.ValueKind != JsonValueKind.Array)
            return new PositionsResult(true, Array.Empty<PositionSnapshot>(), null);

        var positions = new List<PositionSnapshot>();
        foreach (var p in root.EnumerateArray())
        {
            // Net quantity: prefer an explicit net field, else derive buy-minus-sell (both spelling
            // conventions - flBuyQty/flSellQty and buyQty/sellQty - are tried since neither is
            // confirmed against a real response).
            var quantity = ReadInt(p, "flBuyQty") - ReadInt(p, "flSellQty");
            if (quantity == 0) quantity = ReadInt(p, "netQty");
            if (quantity == 0) quantity = ReadInt(p, "buyQty") - ReadInt(p, "sellQty");
            if (quantity == 0) continue; // fully squared off already

            var kotakSegment = ReadString(p, "exSeg") ?? ReadString(p, "exchangeSegment") ?? "";
            var exchange = KotakSymbology.ToCanonicalExchange(kotakSegment);
            if (!string.Equals(exchange, "NFO", StringComparison.OrdinalIgnoreCase)) continue; // NFO only - see the class doc comment

            var tradingSymbol = ReadString(p, "trdSym") ?? ReadString(p, "tradingSymbol") ?? "";
            var product = KotakSymbology.ToCanonicalProduct(ReadString(p, "prod") ?? ReadString(p, "product") ?? "");

            var avgPrice = ReadDecimal(p, "avgNetPrc") ?? ReadDecimal(p, "buyAvgPrc") ?? ReadDecimal(p, "avgPrice") ?? 0m;
            var pnl = ReadDecimal(p, "pnl") ?? ReadDecimal(p, "urPnl") ?? 0m;

            var instrument = _instrumentMaster.Find(tradingSymbol, exchange);
            var token = instrument?.InstrumentToken ?? 0;
            var lotSize = instrument?.LotSize ?? 0;

            Tick? tick = token != 0 && _tickStore.TryGet(token, out var t) ? t : null;
            var lastPrice = tick?.LastPrice ?? 0m;
            var (exitPrice, exitPnl) = PositionExitPricing.Compute(quantity, avgPrice, tick);

            positions.Add(new PositionSnapshot(
                tradingSymbol, exchange, product, quantity, avgPrice, lastPrice, pnl, lotSize, exitPrice, exitPnl, token));
        }

        return new PositionsResult(true, positions, null);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
            : null;

    private static int ReadInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
