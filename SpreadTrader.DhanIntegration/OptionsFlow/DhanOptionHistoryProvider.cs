using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.DhanIntegration.OptionsFlow;

/// <summary>
/// Fetches 1-minute option history from Dhan's intraday chart endpoint
/// (POST /v2/charts/intraday), which is what lets an Options Flow series start at the session open
/// and be rebuilt from scratch whenever the ATM window shifts.
///
/// One minute is the floor: the endpoint accepts intervals of 1, 5, 15, 25 or 60 minutes and has
/// nothing finer, which is precisely why this whole feature works in minute bars.
///
/// RESPONSE SHAPE. Dhan returns the candles COLUMNAR - parallel arrays of open/high/low/close/
/// volume/timestamp rather than an array of candle objects. Both shapes are accepted here anyway:
/// the columnar form is what the endpoint documents, and the row form is cheap to tolerate in case
/// a future revision changes it. Anything else is reported as a parse failure rather than silently
/// producing an empty series, because "this strike has no history" and "we could not read the
/// response" lead to very different diagnoses when a chart looks wrong.
///
/// Requests are paced by a shared RateLimiter: rebuilding a window means one call per newly added
/// strike, and Dhan's data-API limits are per-second - firing 30+ at once would get several of
/// them rejected and leave holes in the series.
/// </summary>
public sealed class DhanOptionHistoryProvider : IOptionHistoryProvider
{
    /// <summary>Dhan's instrument type for the index option contracts this add-on charts.</summary>
    private const string IndexOptionInstrumentType = "OPTIDX";

    /// <summary>IST is UTC+5:30 with no DST; Dhan's chart dates and timestamps are IST.</summary>
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    private readonly DhanApiClient _api;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<DhanOptionHistoryProvider> _logger;

    public DhanOptionHistoryProvider(DhanApiClient api, RateLimiter rateLimiter, ILogger<DhanOptionHistoryProvider> logger)
    {
        _api = api;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<OptionBar>> GetMinuteBarsAsync(
        OptionContract contract,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (!_api.HasSession) return Array.Empty<OptionBar>();

        await _rateLimiter.WaitAsync(cancellationToken);

        var payload = new
        {
            securityId = contract.InstrumentToken.ToString(CultureInfo.InvariantCulture),
            exchangeSegment = contract.Segment,
            instrument = IndexOptionInstrumentType,
            interval = 1,
            oi = false,
            fromDate = ToIstDate(fromUtc),
            toDate = ToIstDate(toUtc),
        };

        using var response = await _api.SendAsync(
            HttpMethod.Post, "/charts/intraday", payload,
            $"fetching intraday history for {contract.TradingSymbol}", cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("Options Flow: history fetch failed for {Symbol}: {Message}",
                contract.TradingSymbol, response.ErrorMessage);
            return Array.Empty<OptionBar>();
        }

        var bars = Parse(response.Root, contract, fromUtc, toUtc);
        return bars;
    }

    /// <summary>Dhan's chart endpoints take plain IST calendar dates, not timestamps.</summary>
    private static string ToIstDate(DateTimeOffset utc) =>
        utc.ToOffset(IstOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private IReadOnlyList<OptionBar> Parse(JsonElement root, OptionContract contract, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        // Columnar form: { "open":[...], "high":[...], ..., "timestamp":[...] }
        if (root.ValueKind == JsonValueKind.Object &&
            TryReadArray(root, "timestamp", out var timestamps) &&
            TryReadArray(root, "open", out var opens) &&
            TryReadArray(root, "high", out var highs) &&
            TryReadArray(root, "low", out var lows) &&
            TryReadArray(root, "close", out var closes))
        {
            TryReadArray(root, "volume", out var volumes);

            var count = new[] { timestamps.Count, opens.Count, highs.Count, lows.Count, closes.Count }.Min();
            var bars = new List<OptionBar>(count);

            for (var i = 0; i < count; i++)
            {
                var minute = FromEpochSeconds(timestamps[i]);
                if (minute < fromUtc || minute > toUtc) continue;

                bars.Add(new OptionBar(
                    minute,
                    opens[i], highs[i], lows[i], closes[i],
                    volumes is not null && i < volumes.Count ? (long)volumes[i] : 0L));
            }

            return bars;
        }

        // An empty object is Dhan's answer for a contract that simply hasn't traded - a normal
        // result for an illiquid far strike, not a failure worth logging loudly.
        if (root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any())
            return Array.Empty<OptionBar>();

        _logger.LogWarning(
            "Options Flow: could not read the history response for {Symbol} - unrecognised shape. First 200 chars: {Body}",
            contract.TradingSymbol, Truncate(root.ToString()));

        return Array.Empty<OptionBar>();
    }

    private static bool TryReadArray(JsonElement root, string property, out List<decimal> values)
    {
        values = new List<decimal>();
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in array.EnumerateArray())
        {
            values.Add(item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetDecimal(out var n) => n,
                JsonValueKind.String when decimal.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
                _ => 0m,
            });
        }

        return true;
    }

    /// <summary>Dhan's chart timestamps are epoch SECONDS. Guard the range so a malformed value
    /// can't throw on the backfill path and abort an entire window rebuild.</summary>
    private static DateTimeOffset FromEpochSeconds(decimal value)
    {
        var seconds = (long)value;
        return seconds is > 0 and < 4102444800 // < year 2100
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";
}
