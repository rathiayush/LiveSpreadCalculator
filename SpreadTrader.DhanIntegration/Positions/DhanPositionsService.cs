using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.DhanIntegration.Positions;

/// <summary>
/// Calls Dhan's GET /v2/positions to report live open positions and PNL - the same data Dhan Web's
/// Positions page shows, so the dashboard doesn't need the broker's own app open to check PNL.
///
/// Two differences from Kite's positions response shape the implementation:
///
///  1. NO LAST TRADED PRICE. Kite includes last_price on every position row; Dhan does not return
///     one at all. LastPrice is therefore taken from the live tick store, which already carries
///     these instruments - PositionsCache anchors a subscription for every open position precisely
///     so that data is there (see TickIngestionHostedService's reconciliation loop). A position
///     whose first tick hasn't arrived yet reports 0, exactly as the Kite path does when the
///     instrument master hasn't resolved it.
///
///  2. PNL IS SPLIT. Kite gives one combined "pnl"; Dhan reports realizedProfit and
///     unrealizedProfit separately. They are summed here so PositionSnapshot.Pnl keeps the same
///     meaning on both platforms - today's mark-to-market plus what has already been booked.
///
/// Like the Kite implementation, only genuinely open positions (net quantity != 0) on the
/// exchanges this app trades are returned, so a stray hand-placed position in another segment
/// can't land in the dashboard's Total PNL with no context.
/// </summary>
public sealed class DhanPositionsService : IPositionsService
{
    private readonly DhanApiClient _api;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly ITickStore _tickStore;
    private readonly DhanSegmentRegistry _segments;
    private readonly ILogger<DhanPositionsService> _logger;
    private readonly HashSet<string> _trackedExchanges;

    public DhanPositionsService(
        DhanApiClient api,
        IBrokerInstrumentMaster instrumentMaster,
        ITickStore tickStore,
        DhanSegmentRegistry segments,
        ILogger<DhanPositionsService> logger,
        McxOptions? mcx = null)
    {
        _api = api;
        _instrumentMaster = instrumentMaster;
        _tickStore = tickStore;
        _segments = segments;
        _logger = logger;

        // NFO is always tracked; MCX only when the commodity add-on is enabled - same rule as the
        // Kite implementation.
        _trackedExchanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NFO" };
        if (mcx is { Enabled: true })
            _trackedExchanges.Add(mcx.Exchange);
    }

    public async Task<PositionsResult> GetPositionsAsync(CancellationToken cancellationToken)
    {
        if (!_api.HasSession)
            return new PositionsResult(false, Array.Empty<PositionSnapshot>(), "No Dhan access token - complete the login flow first.");

        using var response = await _api.SendAsync(
            HttpMethod.Get, "/positions", null, "fetching positions", cancellationToken);

        if (!response.Success)
            return new PositionsResult(false, Array.Empty<PositionSnapshot>(), response.ErrorMessage);

        // Best-effort lot size lookup so the UI can offer "exit N lots" instead of a raw quantity.
        // Never blocks showing positions/PNL.
        try { await _instrumentMaster.EnsureLoadedAsync(cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure instrument master is loaded while fetching positions - lot sizes may be unavailable.");
        }

        var root = response.Root;
        if (root.ValueKind != JsonValueKind.Array)
        {
            // Dhan answers an account with no positions with an empty body or null rather than an
            // empty array - that's "no positions", not a failure.
            return new PositionsResult(true, Array.Empty<PositionSnapshot>(), null);
        }

        var positions = new List<PositionSnapshot>();
        foreach (var p in root.EnumerateArray())
        {
            var quantity = ReadInt(p, "netQty");
            if (quantity == 0) continue; // fully squared off already - nothing to show/act on

            var dhanSegment = ReadString(p, "exchangeSegment") ?? "";
            var exchange = DhanSymbology.ToCanonicalExchange(dhanSegment);
            if (!_trackedExchanges.Contains(exchange)) continue;

            var tradingSymbol = ReadString(p, "tradingSymbol") ?? "";
            var product = DhanSymbology.ToCanonicalProduct(ReadString(p, "productType") ?? "");

            // costPrice is the net average for the position. Fall back to the side-specific average
            // for a position that only ever traded one way, which is the usual case here.
            var avgPrice = ReadDecimal(p, "costPrice")
                           ?? (quantity > 0 ? ReadDecimal(p, "buyAvg") : ReadDecimal(p, "sellAvg"))
                           ?? 0m;

            var pnl = (ReadDecimal(p, "realizedProfit") ?? 0m) + (ReadDecimal(p, "unrealizedProfit") ?? 0m);

            // Prefer the securityId Dhan reports over a symbol lookup - it is exactly the tick-store
            // key, and needs no symbol-spelling assumptions.
            var instrument = _instrumentMaster.Find(tradingSymbol, exchange);
            var token = ReadUInt(p, "securityId") ?? instrument?.InstrumentToken ?? 0;
            var lotSize = instrument?.LotSize ?? 0;

            // Teach the feed client this token's segment. Dhan subscriptions are keyed by
            // (segment, securityId) and the feed otherwise resolves the segment from the
            // futures-only instrument master - so without this, an open position that isn't a
            // future in that master (an option leg placed by hand, say) could never be subscribed,
            // and its row would sit with no live price, exit price or exit PNL while the same
            // position streams fine on Kite. See DhanSegmentRegistry.
            _segments.Remember(token, dhanSegment);

            Tick? tick = token != 0 && _tickStore.TryGet(token, out var t) ? t : null;
            var lastPrice = tick?.LastPrice ?? 0m;
            var (exitPrice, exitPnl) = PositionExitPricing.Compute(quantity, avgPrice, tick);

            positions.Add(new PositionSnapshot(
                tradingSymbol, exchange, product, quantity, avgPrice, lastPrice, pnl, lotSize, exitPrice, exitPnl,
                // Dhan reports securityId on the position row itself, so this needs no symbol-spelling
                // assumption at all - see PositionSnapshot.InstrumentToken.
                token));
        }

        return new PositionsResult(true, positions, null);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static int ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>Dhan returns securityId as a string on some endpoints and a number on others.</summary>
    private static uint? ReadUInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt32(out var number) => number,
            JsonValueKind.String when uint.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
