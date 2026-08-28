using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.DhanIntegration;

/// <summary>
/// Dhan's Intraday Historical Data API (POST /v2/charts/intraday), used only by CdvHostedService to
/// reconstruct a previous day's CDV curve - see IHistoricalCandleSource's doc comment. One
/// instrument per call (Dhan documents no batching for this endpoint, unlike the Market Quote
/// APIs), so callers are responsible for their own pacing across many instruments - see
/// CdvHostedService's backfill loop.
///
/// Request/response shapes confirmed against Dhan's own docs:
///   request:  {"securityId","exchangeSegment","instrument","interval","fromDate","toDate"}
///             fromDate/toDate are "yyyy-MM-dd HH:mm:ss" IST wall-clock, not UTC.
///   response: {"open":[...],"high":[...],"low":[...],"close":[...],"volume":[...],"timestamp":[...]}
///             - parallel arrays, one entry per candle; timestamp is Unix epoch seconds.
/// </summary>
public sealed class DhanHistoricalDataService : IHistoricalCandleSource
{
    // NSE's regular session. Good enough for this app's purposes (index options only) - no attempt
    // to special-case a shortened/extended session, same "does not account for exchange holidays"
    // tradeoff already accepted elsewhere in this app (see EngineOptions.SpreadHistoryWeekdaysOnly).
    private const string SessionStart = "09:15:00";
    private const string SessionEnd = "15:30:00";

    // CDV only ever deals in index options (OPTIDX) - see CdvOptions.Underlyings - so this is never
    // parameterized; a future non-index underlying would need this passed in instead.
    private const string Instrument = "OPTIDX";

    private readonly DhanApiClient _api;
    private readonly ILogger<DhanHistoricalDataService> _logger;

    public DhanHistoricalDataService(DhanApiClient api, ILogger<DhanHistoricalDataService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HistoricalCandle>> GetIntradayCandlesAsync(
        uint instrumentToken, string brokerSegment, DateOnly day, int intervalMinutes, CancellationToken cancellationToken)
    {
        if (!_api.HasSession)
        {
            _logger.LogWarning("CDV backfill: cannot fetch intraday candles for {Segment}:{Token} - Dhan is not authenticated.", brokerSegment, instrumentToken);
            return Array.Empty<HistoricalCandle>();
        }

        var payload = new
        {
            securityId = instrumentToken.ToString(CultureInfo.InvariantCulture),
            exchangeSegment = brokerSegment,
            instrument = Instrument,
            interval = intervalMinutes.ToString(CultureInfo.InvariantCulture),
            fromDate = $"{day:yyyy-MM-dd} {SessionStart}",
            toDate = $"{day:yyyy-MM-dd} {SessionEnd}",
        };

        using var response = await _api.SendAsync(
            HttpMethod.Post, "/charts/intraday", payload,
            $"CDV backfill ({brokerSegment}:{instrumentToken}, {day:yyyy-MM-dd})", cancellationToken);

        if (!response.Success)
        {
            // Routine for a strike that simply didn't exist / didn't trade that day (a market
            // holiday, a strike added later in the chain) - logged at Debug via the caller's own
            // aggregate summary instead of one Warning per contract, which would be pure noise
            // across the ~30+ strikes CdvHostedService backfills per underlying.
            _logger.LogDebug("CDV backfill: intraday request failed for {Segment}:{Token} on {Day} - {Error}",
                brokerSegment, instrumentToken, day, response.ErrorMessage);
            return Array.Empty<HistoricalCandle>();
        }

        return ParseCandles(response.Root, brokerSegment, instrumentToken);
    }

    private List<HistoricalCandle> ParseCandles(JsonElement root, string brokerSegment, uint instrumentToken)
    {
        var candles = new List<HistoricalCandle>();

        if (!root.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
            return candles;

        var open = GetArray(root, "open");
        var high = GetArray(root, "high");
        var low = GetArray(root, "low");
        var close = GetArray(root, "close");
        var volume = GetArray(root, "volume");

        var count = timestamps.GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            try
            {
                var epochSeconds = timestamps[i].GetInt64();
                candles.Add(new HistoricalCandle(
                    Time: DateTimeOffset.FromUnixTimeSeconds(epochSeconds),
                    Open: i < open.Count ? open[i] : 0m,
                    High: i < high.Count ? high[i] : 0m,
                    Low: i < low.Count ? low[i] : 0m,
                    Close: i < close.Count ? close[i] : 0m,
                    Volume: i < volume.Count ? (long)volume[i] : 0));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            {
                _logger.LogDebug(ex, "CDV backfill: skipped an unparsable candle at index {Index} for {Segment}:{Token}", i, brokerSegment, instrumentToken);
            }
        }

        return candles;
    }

    private static List<decimal> GetArray(JsonElement root, string propertyName)
    {
        var result = new List<decimal>();
        if (root.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
                result.Add(element.ValueKind == JsonValueKind.Number ? element.GetDecimal() : 0m);
        }
        return result;
    }
}
