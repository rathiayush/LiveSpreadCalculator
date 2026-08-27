using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KotakIntegration.Instruments;

/// <summary>
/// Downloads and caches Kotak Neo's scrip master so a tradingsymbol can be resolved to the numeric
/// token the order API and market-data feed key off - the Kotak counterpart of
/// InstrumentMasterService/DhanInstrumentMasterService, honouring the same IBrokerInstrumentMaster
/// contract.
///
/// TWO-STEP DOWNLOAD, unlike Kite's single exchange-scoped endpoint or Dhan's one flat file: Kotak
/// first returns a JSON list of per-exchange-segment CSV file URLs (GET
/// script-details/1.0/masterscrip/file-paths, authenticated with just the consumer key - see
/// KotakApiClient.SendConsumerKeyAsync), then each configured segment's CSV is downloaded
/// separately and parsed, keeping only futures rows exactly like the other two integrations do.
///
/// NO MCX. Only "NFO" (Kotak segment "nse_fo") is ever requested, regardless of McxOptions - see
/// KotakSymbology's class doc comment: Kotak Neo's documented exchange-segment list has no MCX
/// entry, so there is no file to even ask for.
///
/// COLUMN LAYOUT: pSymbol (token), pTrdSymbol (trading symbol) and lLotSize (lot size) are now
/// CONFIRMED by Kotak's own Scrip Master API docs ("Response CSV Files column mapping" table) and
/// are tried first in ParseCsv; lExpiryDate is confirmed too but is an EPOCH value requiring a
/// segment-specific offset before it converts to a real date - see ParseExpiry. Everything else
/// (instrument type, underlying name, tick size) isn't covered by that table and remains a best-
/// effort guess, using the same alias-resolution pattern DhanInstrumentMasterService uses for its
/// compact/detailed column differences. A file whose real columns don't match any candidate fails
/// loudly (logged, zero instruments loaded) rather than silently parsing garbage - see the
/// required-column check in ParseCsv.
/// </summary>
public sealed class KotakInstrumentMasterService : IBrokerInstrumentMaster
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly KotakApiClient _api;
    private readonly KotakOptions _options;
    private readonly ILogger<KotakInstrumentMasterService> _logger;
    private readonly string _futuresCsvPath;
    private readonly HashSet<string> _exchanges;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<BrokerInstrument> _instruments = new();

    /// <summary>Cash equities + index levels from Kotak's nse_cm segment file, held separately from
    /// the futures above so every futures lookup keeps its futures-only meaning. Read only through
    /// FindSpot - see the IBrokerInstrumentMaster doc comment.</summary>
    private List<BrokerInstrument> _spotInstruments = new();
    private Dictionary<string, BrokerInstrument> _bySymbol = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <param name="exchanges">Canonical exchange codes to retain. Kotak has no MCX segment (see
    /// the class doc comment), so anything other than "NFO" here is silently dropped when resolving
    /// which segment file(s) to download - see ResolveRequestedSegments.</param>
    public KotakInstrumentMasterService(
        HttpClient httpClient,
        KotakApiClient api,
        IOptions<KotakOptions> options,
        ILogger<KotakInstrumentMasterService> logger,
        string futuresCsvPath,
        IReadOnlyList<string>? exchanges = null)
    {
        _httpClient = httpClient;
        _api = api;
        _options = options.Value;
        _logger = logger;
        _futuresCsvPath = futuresCsvPath;
        _exchanges = new HashSet<string>(
            exchanges is { Count: > 0 } ? exchanges : new[] { "NFO" },
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (IsFresh()) return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh()) return;

            try
            {
                await RefreshFromKotakAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_instruments.Count == 0) throw;

                _logger.LogWarning(ex,
                    "Live Kotak scrip master check failed - continuing with the {Count} futures already loaded until the next refresh attempt.",
                    _instruments.Count);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task RefreshFromKotakAsync(CancellationToken cancellationToken)
    {
        var segments = ResolveRequestedSegments();
        if (segments.Count == 0)
        {
            _logger.LogWarning("No Kotak-supported exchange segments configured (NFO maps to nse_fo; MCX has no Kotak segment) - nothing to download.");
            return;
        }

        var filePaths = await FetchFilePathsAsync(cancellationToken);

        var parsed = new List<(BrokerInstrument Instrument, string? Alias)>();
        foreach (var segment in segments)
        {
            if (!filePaths.TryGetValue(segment, out var url))
            {
                _logger.LogWarning("Kotak's scrip master file-path list had no entry for segment '{Segment}'.", segment);
                continue;
            }

            _logger.LogInformation("Downloading Kotak scrip master for {Segment} from {Url}...", segment, url);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            parsed.AddRange(ParseCsv(stream, segment));
        }

        _instruments = parsed.Select(p => p.Instrument).ToList();
        _bySymbol = BuildSymbolIndex(parsed);
        _spotInstruments = await LoadSpotInstrumentsAsync(filePaths, cancellationToken);
        _loadedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Instrument master ready: {Count} futures across [{Segments}], plus {SpotCount} cash/index instruments for spot pricing (options discarded during parse)",
            _instruments.Count, string.Join(", ", segments), _spotInstruments.Count);

        if (_instruments.Count == 0)
        {
            _logger.LogWarning(
                "Kotak scrip master parsed to zero futures contracts - the column layout may not match what ParseCsv expects (see this " +
                "class's doc comment on column-name candidates being best-effort/unconfirmed).");
        }
    }

    /// <summary>
    /// Downloads Kotak's cash segment ("nse_cm") for spot pricing - already present in the same
    /// file-paths list the futures segments come from, so this needs no extra API call, just one
    /// extra file.
    ///
    /// Best-effort: spot is a display nicety, so any failure here logs and yields an empty list
    /// rather than propagating and taking down the futures universe the app actually trades on.
    /// </summary>
    private async Task<List<BrokerInstrument>> LoadSpotInstrumentsAsync(
        Dictionary<string, string> filePaths,
        CancellationToken cancellationToken)
    {
        var spotSegment = KotakSymbology.ToKotakSegment("NSE"); // "nse_cm"

        if (!filePaths.TryGetValue(spotSegment, out var url))
        {
            _logger.LogWarning(
                "Kotak's scrip master file-path list had no '{Segment}' entry - spot prices and basis % will be unavailable. " +
                "Futures resolution and trading are unaffected.", spotSegment);
            return new List<BrokerInstrument>();
        }

        try
        {
            _logger.LogInformation("Downloading Kotak scrip master for {Segment} (cash + indices) from {Url}...", spotSegment, url);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return ParseSpotCsv(stream, spotSegment);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not load Kotak's {Segment} scrip master - spot prices and basis % will be unavailable. " +
                "Futures resolution and trading are unaffected.", spotSegment);
            return new List<BrokerInstrument>();
        }
    }

    /// <summary>Same file layout as ParseCsv but WITHOUT its futures filter - in the cash segment
    /// every row is already a spot instrument (equity or index), which is exactly what's wanted
    /// here.</summary>
    private List<BrokerInstrument> ParseSpotCsv(Stream stream, string kotakSegment)
    {
        var result = new List<BrokerInstrument>();
        using var reader = new StreamReader(stream);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            _logger.LogWarning("Kotak scrip master for {Segment} was empty - no header row.", kotakSegment);
            return result;
        }

        var header = BuildHeaderMap(SplitCsvLine(headerLine));
        var tokenIndex = Resolve(header, Columns.Token);
        var symbolIndex = Resolve(header, Columns.TradingSymbol);
        var underlyingIndex = Resolve(header, Columns.UnderlyingName);

        if (tokenIndex < 0 || symbolIndex < 0)
        {
            _logger.LogWarning(
                "Kotak scrip master for {Segment} is missing an instrument token or trading symbol column - no spot instruments loaded. Header was: {Header}",
                kotakSegment, headerLine);
            return result;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);

            if (!uint.TryParse(Get(fields, tokenIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var token))
                continue;

            var tradingSymbol = Get(fields, symbolIndex);
            if (string.IsNullOrWhiteSpace(tradingSymbol)) continue;

            var name = Get(fields, underlyingIndex);
            if (string.IsNullOrWhiteSpace(name)) name = tradingSymbol;

            result.Add(new BrokerInstrument(
                InstrumentToken: token,
                TradingSymbol: tradingSymbol,
                Name: name,
                Exchange: KotakSymbology.ToCanonicalExchange(kotakSegment),
                Segment: kotakSegment,
                InstrumentType: "EQ",
                Expiry: null,
                LotSize: 0,
                TickSize: 0m));
        }

        return result;
    }

    /// <summary>Matches a futures underlying to its cash/index row - see SpotSymbology. Checks both
    /// the trading symbol and the name so an index listed under either spelling resolves.</summary>
    public IReadOnlyList<BrokerInstrument> GetAllSpot() => _spotInstruments;

    public BrokerInstrument? FindSpot(string underlyingName)
    {
        foreach (var candidate in SpotSymbology.CandidateSpotNames(underlyingName))
        {
            var match = _spotInstruments.FirstOrDefault(i =>
                string.Equals(i.TradingSymbol, candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>NFO -&gt; Kotak's "nse_fo" segment, dropped entirely for anything with no Kotak
    /// mapping (MCX). Kept as its own method so this "MCX is silently unsupported here" decision is
    /// in exactly one place.</summary>
    private List<string> ResolveRequestedSegments() =>
        _exchanges
            .Where(e => !string.Equals(e, "MCX", StringComparison.OrdinalIgnoreCase))
            .Select(KotakSymbology.ToKotakSegment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>GET script-details/1.0/masterscrip/file-paths -&gt; { data: { filesPaths: [url,
    /// url, ...] } }, CONFIRMED against Kotak's own Scrip Master API docs - filesPaths is a flat
    /// array of download URLs, one per segment, with NO segment field alongside each url; the
    /// segment has to be read off the URL's own filename instead (e.g. ".../transformed/nse_fo.csv"
    /// -&gt; "nse_fo", ".../transformed-v1/nse_cm-v1.csv" -&gt; "nse_cm" after stripping the "-v1"
    /// suffix the cash-segment files carry). This replaces an earlier guess (object keyed by segment,
    /// or an array of {segment,url} pairs) that didn't match either - the entire instrument master
    /// would have loaded zero segments against a real account until this was confirmed.</summary>
    private async Task<Dictionary<string, string>> FetchFilePathsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var response = await _api.SendConsumerKeyAsync(
            HttpMethod.Get, "script-details/1.0/masterscrip/file-paths", null, "fetching Kotak scrip master file paths", cancellationToken);

        if (!response.Success)
        {
            _logger.LogError("Could not fetch Kotak's scrip master file-path list: {Message}", response.ErrorMessage);
            return result;
        }

        var data = response.Data;
        var filesPaths = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("filesPaths", out var fp) ? fp : data;

        if (filesPaths.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in filesPaths.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) continue;
                var url = entry.GetString();
                var segment = ExtractSegmentFromUrl(url);
                if (!string.IsNullOrWhiteSpace(segment) && !string.IsNullOrWhiteSpace(url))
                    result[segment] = url;
            }
        }

        return result;
    }

    /// <summary>".../transformed/nse_fo.csv" -&gt; "nse_fo"; ".../transformed-v1/nse_cm-v1.csv" -&gt;
    /// "nse_cm" - the segment is the URL's filename, minus the .csv extension and an optional
    /// trailing "-v1" the cash-segment files carry (see FetchFilePathsAsync's doc comment).</summary>
    private static string? ExtractSegmentFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var fileName = url[(url.LastIndexOf('/') + 1)..];
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^4];
        if (fileName.EndsWith("-v1", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^3];
        return fileName;
    }

    private bool IsFresh() => _instruments.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime;

    private static Dictionary<string, BrokerInstrument> BuildSymbolIndex(IReadOnlyList<(BrokerInstrument Instrument, string? Alias)> parsed)
    {
        var index = new Dictionary<string, BrokerInstrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instrument, _) in parsed)
            index[Key(instrument.TradingSymbol, instrument.Exchange)] = instrument;
        foreach (var (instrument, alias) in parsed)
            if (!string.IsNullOrWhiteSpace(alias)) index.TryAdd(Key(alias, instrument.Exchange), instrument);
        return index;
    }

    private static string Key(string symbol, string exchange) => $"{symbol}|{exchange}";

    public BrokerInstrument? Find(string tradingSymbol, string exchange)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol)) return null;
        if (_bySymbol.TryGetValue(Key(tradingSymbol, exchange), out var match)) return match;
        var canonical = KotakSymbology.ToCanonicalExchange(exchange);
        return _bySymbol.TryGetValue(Key(tradingSymbol, canonical), out var normalized) ? normalized : null;
    }

    public IReadOnlyList<BrokerInstrument> FindFutures(string underlyingName, string exchange = "NFO") =>
        _instruments
            .Where(i => i.Exchange.Equals(exchange, StringComparison.OrdinalIgnoreCase)
                        && i.Name.Equals(underlyingName, StringComparison.OrdinalIgnoreCase)
                        && i.Expiry is not null)
            .OrderBy(i => i.Expiry)
            .ToArray();

    public IReadOnlyList<BrokerInstrument> GetAllFutures(string? exchange = null) =>
        _instruments
            .Where(i => exchange is null || i.Exchange.Equals(exchange, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name).ThenBy(i => i.Expiry)
            .ToArray();

    public IReadOnlyList<string> GetAllFuturesUnderlyings(string exchange = "NFO") =>
        _instruments
            .Where(i => i.Exchange.Equals(exchange, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<IReadOnlyList<BrokerInstrument>> FetchAndSaveFuturesAsync(string? exchange, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var futures = GetAllFutures(exchange);
        await SaveFuturesToCsvAsync(futures, cancellationToken);
        _logger.LogInformation("Saved {Count} futures contracts to {Path}", futures.Count, _futuresCsvPath);
        return futures;
    }

    public async Task ResolveAsync(InstrumentConfig config, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var match = Find(config.TradingSymbol, config.Exchange)
            ?? throw new InvalidOperationException($"Instrument not found in Kotak scrip master: {config.TradingSymbol} on {config.Exchange}");

        config.InstrumentToken = match.InstrumentToken;
        config.Expiry = match.Expiry;
        config.Underlying ??= match.Name;
        config.LotSize = match.LotSize;
        config.TickSize = match.TickSize;
    }

    private async Task SaveFuturesToCsvAsync(IReadOnlyList<BrokerInstrument> futures, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_futuresCsvPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("instrument_token,tradingsymbol,name,exchange,segment,expiry,lot_size");
        foreach (var i in futures)
        {
            sb.AppendLine(string.Join(",",
                i.InstrumentToken, EscapeCsv(i.TradingSymbol), EscapeCsv(i.Name), i.Exchange, i.Segment,
                i.Expiry?.ToString("yyyy-MM-dd") ?? "", i.LotSize));
        }

        var tempPath = _futuresCsvPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, sb.ToString(), cancellationToken);
        File.Move(tempPath, _futuresCsvPath, overwrite: true);
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    // ---- CSV parsing (best-effort column names - see the class doc comment) ---------------------

    private static class Columns
    {
        // pSymbol, pTrdSymbol and lLotSize are CONFIRMED by Kotak's own Scrip Master API docs
        // (the "Response CSV Files column mapping" table) - listed first; the rest remain best-
        // effort guesses (InstrumentType/UnderlyingName/TickSize aren't covered by that table at
        // all) so the alias fallback stays in place for them.
        public static readonly string[] Token = { "pSymbol", "instrumentToken", "token", "scripCode" };
        public static readonly string[] TradingSymbol = { "pTrdSymbol", "tradingSymbol", "trdSym", "symbol" };
        public static readonly string[] InstrumentType = { "pInstType", "instrumentType", "instType" };
        public static readonly string[] UnderlyingName = { "pSymbolName", "symbolName", "underlying" };
        // lExpiryDate is confirmed but is an EPOCH value, not a date string - see ParseExpiry.
        public static readonly string[] Expiry = { "lExpiryDate", "pExpiryDate", "expiryDate", "expiry" };
        public static readonly string[] LotSize = { "lLotSize", "pLotSize", "lotSize", "lot_size" };
        public static readonly string[] TickSize = { "pTickSize", "tickSize", "tick_size" };
    }

    private List<(BrokerInstrument Instrument, string? Alias)> ParseCsv(Stream stream, string kotakSegment)
    {
        var result = new List<(BrokerInstrument Instrument, string? Alias)>();
        var canonicalExchange = KotakSymbology.ToCanonicalExchange(kotakSegment);
        using var reader = new StreamReader(stream);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            _logger.LogError("Kotak scrip master for {Segment} was empty - no header row.", kotakSegment);
            return result;
        }

        var header = BuildHeaderMap(SplitCsvLine(headerLine));

        var tokenIndex = Resolve(header, Columns.Token);
        var symbolIndex = Resolve(header, Columns.TradingSymbol);
        var instrumentTypeIndex = Resolve(header, Columns.InstrumentType);
        var underlyingIndex = Resolve(header, Columns.UnderlyingName);
        var (expiryIndex, expiryColumnName) = ResolveNamed(header, Columns.Expiry);
        var lotSizeIndex = Resolve(header, Columns.LotSize);
        var tickSizeIndex = Resolve(header, Columns.TickSize);

        if (tokenIndex < 0 || symbolIndex < 0)
        {
            _logger.LogError(
                "Kotak scrip master for {Segment} is missing required columns (need an instrument token and a trading symbol) - " +
                "no instruments were loaded from this file. Column-name candidates tried: token={TokenCandidates}, symbol={SymbolCandidates}. Header was: {Header}",
                kotakSegment, string.Join("/", Columns.Token), string.Join("/", Columns.TradingSymbol), headerLine);
            return result;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);

            var instrumentType = Get(fields, instrumentTypeIndex);
            if (instrumentTypeIndex >= 0 && !IsFuture(instrumentType)) continue;

            if (!uint.TryParse(Get(fields, tokenIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var token))
                continue;

            var tradingSymbol = Get(fields, symbolIndex);
            if (string.IsNullOrWhiteSpace(tradingSymbol)) continue;

            // Fall back to option/future rows only when instrument type wasn't even a resolvable
            // column - in that case every row that reaches here is kept, on the assumption the
            // downloaded file already is futures-only for this endpoint.
            var underlying = Get(fields, underlyingIndex);
            if (string.IsNullOrWhiteSpace(underlying))
                underlying = DeriveUnderlying(tradingSymbol);

            result.Add((new BrokerInstrument(
                InstrumentToken: token,
                TradingSymbol: tradingSymbol,
                Name: underlying,
                Exchange: canonicalExchange,
                Segment: kotakSegment,
                InstrumentType: "FUT",
                Expiry: ParseExpiry(Get(fields, expiryIndex), expiryColumnName, kotakSegment),
                LotSize: ParseLotSize(Get(fields, lotSizeIndex)),
                TickSize: ParseDecimal(Get(fields, tickSizeIndex))), null));
        }

        return result;
    }

    private static bool IsFuture(string? instrumentType) => instrumentType?.ToUpperInvariant() switch
    {
        "FUT" or "FUTSTK" or "FUTIDX" or "FUTCOM" or "FUTCUR" => true,
        _ => false,
    };

    private static string DeriveUnderlying(string tradingSymbol)
    {
        var dash = tradingSymbol.IndexOfAny(new[] { '-', ' ' });
        return dash > 0 ? tradingSymbol[..dash] : tradingSymbol;
    }

    private static Dictionary<string, int> BuildHeaderMap(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim().Trim('﻿');
            if (name.Length > 0) map.TryAdd(name, i);
        }
        return map;
    }

    private static int Resolve(Dictionary<string, int> header, string[] candidates)
    {
        foreach (var candidate in candidates)
            if (header.TryGetValue(candidate, out var index)) return index;
        return -1;
    }

    /// <summary>Same as <see cref="Resolve"/> but also returns which candidate matched, so a caller
    /// (ParseExpiry) can tell the confirmed epoch column (lExpiryDate) apart from the older guessed
    /// date-string ones without re-deriving that from the raw value alone.</summary>
    private static (int Index, string? Name) ResolveNamed(Dictionary<string, int> header, string[] candidates)
    {
        foreach (var candidate in candidates)
            if (header.TryGetValue(candidate, out var index)) return (index, candidate);
        return (-1, null);
    }

    private static string? Get(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index].Trim() : null;

    /// <summary>lExpiryDate (the confirmed column) is an EPOCH value, not a date string - per
    /// Kotak's Scrip Master docs: nse_fo/cde_fo need <c>epoch + 315511200</c> before converting to
    /// IST; mcx_fo/bse_fo convert the raw epoch directly. Any other (guessed, unconfirmed) column
    /// name falls back to the old plain-date-string parsing, since only lExpiryDate itself is known
    /// to need the epoch treatment.</summary>
    private static DateOnly? ParseExpiry(string? raw, string? columnName, string kotakSegment)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (string.Equals(columnName, "lExpiryDate", StringComparison.OrdinalIgnoreCase))
            return ParseEpochExpiry(raw, kotakSegment);

        var datePart = raw.Length > 10 ? raw[..10] : raw;
        if (DateOnly.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)
            ? DateOnly.FromDateTime(dateTime)
            : null;
    }

    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    private static DateOnly? ParseEpochExpiry(string raw, string kotakSegment)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)) return null;

        if (kotakSegment.Equals("nse_fo", StringComparison.OrdinalIgnoreCase) ||
            kotakSegment.Equals("cde_fo", StringComparison.OrdinalIgnoreCase))
            epoch += 315511200;

        try
        {
            var ist = DateTimeOffset.FromUnixTimeSeconds(epoch).ToOffset(IstOffset);
            return DateOnly.FromDateTime(ist.DateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ParseLotSize(string? raw)
    {
        var value = ParseDecimal(raw);
        return value > 0 && value <= int.MaxValue ? (int)value : 0;
    }

    private static decimal ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
