using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration;

namespace SpreadTrader.KiteIntegration.Instruments;

/// <summary>
/// Downloads and caches Kite's full instrument master (a CSV dump of every tradeable
/// instrument across all exchanges) so tradingsymbols like "RELIANCE26JULFUT" can be resolved
/// to the numeric instrument_token the WebSocket API and instrument lookups require.
/// https://kite.trade/docs/connect/v3/market-quotes/#retrieving-the-full-instrument-list
/// </summary>
public sealed class InstrumentMasterService
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly ILogger<InstrumentMasterService> _logger;
    private readonly string _futuresCsvPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<KiteInstrument> _instruments = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public InstrumentMasterService(
        HttpClient httpClient,
        IOptions<KiteOptions> options,
        ILogger<InstrumentMasterService> logger,
        string futuresCsvPath)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _futuresCsvPath = futuresCsvPath;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_instruments.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromHours(12))
            return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_instruments.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromHours(12))
                return;

            _logger.LogInformation("Downloading Kite instrument master...");
            using var response = await _httpClient.GetAsync($"{_options.RestBaseUrl}/instruments", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            _instruments = ParseCsv(stream);
            _loadedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Loaded {Count} instruments from Kite", _instruments.Count);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public KiteInstrument? Find(string tradingSymbol, string exchange) =>
        _instruments.FirstOrDefault(i =>
            string.Equals(i.TradingSymbol, tradingSymbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Exchange, exchange, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the futures contracts for an underlying, ordered by expiry ascending -
    /// handy for picking "near month" (index 0) and "far month" (index 1) automatically.</summary>
    public IReadOnlyList<KiteInstrument> FindFutures(string underlyingName, string exchange = "NFO") =>
        _instruments
            .Where(i => i.Exchange.Equals(exchange, StringComparison.OrdinalIgnoreCase)
                        && i.InstrumentType.Equals("FUT", StringComparison.OrdinalIgnoreCase)
                        && i.Name.Equals(underlyingName, StringComparison.OrdinalIgnoreCase)
                        && i.Expiry is not null)
            .OrderBy(i => i.Expiry)
            .ToArray();

    /// <summary>All futures contracts (no options, no equities/indices) across the given
    /// exchange, or every exchange if none is given. Ordered for readability.</summary>
    public IReadOnlyList<KiteInstrument> GetAllFutures(string? exchange = null) =>
        _instruments
            .Where(i => i.InstrumentType.Equals("FUT", StringComparison.OrdinalIgnoreCase)
                        && (exchange is null || i.Exchange.Equals(exchange, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Expiry)
            .ToArray();

    /// <summary>Every distinct underlying that currently has at least one listed futures
    /// contract on the given exchange - i.e. the full real-time set of stocks/indices Kite
    /// actually offers futures for right now, as opposed to a hand-picked list like a Nifty
    /// index constituents CSV. Alphabetical, since the instrument master carries no market-cap
    /// or liquidity ranking - used by UniverseBuilderService as the "fetch everything available"
    /// symbol source.</summary>
    public IReadOnlyList<string> GetAllFuturesUnderlyings(string exchange = "NFO") =>
        _instruments
            .Where(i => i.Exchange.Equals(exchange, StringComparison.OrdinalIgnoreCase)
                        && i.InstrumentType.Equals("FUT", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Downloads the instrument master if needed, filters to futures contracts only
    /// (explicitly excludes options / equities / indices), and writes them to the configured
    /// futures CSV file (Storage:FuturesCsvPath in appsettings.json) so the symbol list is
    /// available on disk for reference without hitting Kite again.</summary>
    public async Task<IReadOnlyList<KiteInstrument>> FetchAndSaveFuturesAsync(string? exchange, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var futures = GetAllFutures(exchange);
        await SaveFuturesToCsvAsync(futures, cancellationToken);
        _logger.LogInformation("Saved {Count} futures contracts to {Path}", futures.Count, _futuresCsvPath);
        return futures;
    }

    private async Task SaveFuturesToCsvAsync(IReadOnlyList<KiteInstrument> futures, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_futuresCsvPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("instrument_token,tradingsymbol,name,exchange,segment,expiry,lot_size");
        foreach (var i in futures)
        {
            sb.AppendLine(string.Join(",",
                i.InstrumentToken,
                EscapeCsv(i.TradingSymbol),
                EscapeCsv(i.Name),
                i.Exchange,
                i.Segment,
                i.Expiry?.ToString("yyyy-MM-dd") ?? "",
                i.LotSize));
        }

        var tempPath = _futuresCsvPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, sb.ToString(), cancellationToken);
        File.Move(tempPath, _futuresCsvPath, overwrite: true);
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    public async Task ResolveAsync(InstrumentConfig config, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var match = Find(config.TradingSymbol, config.Exchange)
            ?? throw new InvalidOperationException($"Instrument not found in Kite master: {config.TradingSymbol} on {config.Exchange}");

        config.InstrumentToken = match.InstrumentToken;
        config.Expiry = match.Expiry;
        config.Underlying ??= match.Name;
        config.LotSize = match.LotSize;
        config.TickSize = match.TickSize;
    }

    private static List<KiteInstrument> ParseCsv(Stream stream)
    {
        // Columns: instrument_token,exchange_token,tradingsymbol,name,last_price,expiry,strike,
        //          tick_size,lot_size,instrument_type,segment,exchange
        var result = new List<KiteInstrument>();
        using var reader = new StreamReader(stream);
        _ = reader.ReadLine(); // header

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);
            if (fields.Length < 12) continue;
            if (!uint.TryParse(fields[0], out var token)) continue;

            DateOnly? expiry = DateOnly.TryParse(fields[5], out var exp) ? exp : null;
            var lotSize = int.TryParse(fields[8], out var lot) ? lot : 0;
            var tickSize = decimal.TryParse(fields[7], NumberStyles.Number, CultureInfo.InvariantCulture, out var tick) ? tick : 0m;

            result.Add(new KiteInstrument(
                InstrumentToken: token,
                TradingSymbol: fields[2],
                Name: fields[3],
                Exchange: fields[11],
                Segment: fields[10],
                InstrumentType: fields[9],
                Expiry: expiry,
                LotSize: lotSize,
                TickSize: tickSize));
        }

        return result;
    }

    private static string[] SplitCsvLine(string line)
    {
        // Minimal quoted-field CSV split - the instrument dump quotes names containing commas.
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
