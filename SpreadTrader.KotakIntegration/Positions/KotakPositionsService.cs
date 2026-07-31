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
/// FIELD NAMES: exSeg, trdSym, prod, and the flBuyQty/flSellQty/qty quantity fields are CONFIRMED
/// by Kotak's own Positions API docs - qty in particular is documented as the NET position quantity
/// directly, so it's tried before falling back to the flBuyQty-minus-flSellQty derivation this app
/// used before that was confirmed. avgPrice/pnl are NOT covered by that doc's field list at all (no
/// avgPrc-shaped or pnl-shaped field appears anywhere in the confirmed response) - avgPrice is
/// therefore derived from buyAmt/sellAmt (both confirmed) divided by the matching filled quantity
/// instead of guessing an unconfirmed field name; pnl has no such derivation available and stays a
/// best-effort guess like before, since no PNL figure appears in the confirmed docs at all - the
/// live PositionExitPricing.Compute-based ExitPnl below (based on this app's own tick store) is
/// what OrderDispatchHostedService and the dashboard actually rely on.
///
/// LastPrice is taken from the live tick store rather than the response, same as
/// DhanPositionsService (see its doc comment) - PositionsCache anchors a subscription for every
/// open position specifically so that data is there.
///
/// NO MCX. Only NFO is ever tracked here, regardless of McxOptions - see KotakSymbology's class doc
/// comment for why: Kotak Neo's documented exchange-segment list has no MCX entry at all, so an MCX
/// position could never resolve to a valid Kotak segment even if one existed on the account.
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
            // "qty" is the CONFIRMED net position quantity field - tried first, ahead of the
            // flBuyQty-minus-flSellQty derivation this app used before that was confirmed (kept as
            // a fallback for any response shape that omits qty).
            var flBuyQty = ReadInt(p, "flBuyQty");
            var flSellQty = ReadInt(p, "flSellQty");
            var quantity = ReadInt(p, "qty");
            if (quantity == 0) quantity = flBuyQty - flSellQty;
            if (quantity == 0) quantity = ReadInt(p, "netQty");
            if (quantity == 0) quantity = ReadInt(p, "buyQty") - ReadInt(p, "sellQty");
            if (quantity == 0) continue; // fully squared off already

            var kotakSegment = ReadString(p, "exSeg") ?? ReadString(p, "exchangeSegment") ?? "";
            var exchange = KotakSymbology.ToCanonicalExchange(kotakSegment);
            if (!string.Equals(exchange, "NFO", StringComparison.OrdinalIgnoreCase)) continue; // NFO only - see the class doc comment

            var tradingSymbol = ReadString(p, "trdSym") ?? ReadString(p, "tradingSymbol") ?? "";
            var product = KotakSymbology.ToCanonicalProduct(ReadString(p, "prod") ?? ReadString(p, "product") ?? "");

            // No avgPrc-shaped field appears in Kotak's confirmed Positions response at all - derive
            // it instead from buyAmt/sellAmt (both confirmed) over the matching filled quantity,
            // falling back to the old guessed field names only if that derivation isn't possible.
            var buyAmt = ReadDecimal(p, "buyAmt");
            var sellAmt = ReadDecimal(p, "sellAmt");
            var avgPrice =
                quantity > 0 && flBuyQty > 0 ? (buyAmt ?? 0m) / flBuyQty :
                quantity < 0 && flSellQty > 0 ? (sellAmt ?? 0m) / flSellQty :
                ReadDecimal(p, "avgNetPrc") ?? ReadDecimal(p, "buyAvgPrc") ?? ReadDecimal(p, "avgPrice") ?? 0m;
            // pnl: unconfirmed - Kotak's docs list no PNL field for this endpoint at all. Kept as a
            // best-effort guess; ExitPnl (computed below from this app's own live ticks) is what
            // dispatch/dashboard actually rely on.
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
