using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.DhanIntegration.Instruments;

/// <summary>
/// Downloads and caches Dhan's "security id list" (the scrip master CSV) so a tradingsymbol can be
/// resolved to the numeric <c>securityId</c> that Dhan's order API and market-data feed both key
/// off. The Dhan counterpart of InstrumentMasterService, honouring the same
/// IBrokerInstrumentMaster contract so nothing upstream can tell the difference.
///
/// Three things differ from Kite's instrument master and shape this implementation:
///
///  1. NO EXCHANGE-SCOPED ENDPOINT. Kite serves /instruments/NFO; Dhan publishes one flat CSV
///     covering every exchange and instrument type (well over 100k rows, dominated by the options
///     chains). There is nothing to narrow server-side, so ParseCsv discards every non-futures row
///     and every row outside the configured exchanges as it streams, keeping only the few hundred
///     futures contracts this app trades in memory.
///
///  2. THE DETAILED CSV IS REQUIRED. Dhan publishes a compact list and a detailed one. Only the
///     detailed list carries SEM_TICK_SIZE, and order pricing genuinely needs it - a LIMIT price
///     that isn't an exact multiple of the tick size is rejected outright, and tick size varies by
///     contract (see MarketProtectionService). Hence DhanOptions.ScripMasterUrl defaults to the
///     detailed CSV. If a deployment points it at the compact one, tick size comes back 0 and
///     order pricing falls back to its 0.05 default - correct for most NSE F&amp;O, not for all.
///
///  3. COLUMNS ARE READ BY NAME, NOT POSITION. Kite's dump has a fixed, documented column order
///     this codebase indexes into directly. Dhan's has grown columns over time and differs between
///     the compact and detailed files, so the header row is mapped to indices up front and every
///     field is looked up by name (with the compact/detailed aliases both accepted). A file that
///     gains a column, or is swapped between compact and detailed, therefore keeps parsing rather
///     than silently reading the wrong field into lot size or tick size.
///
/// SWITCHING BROKERS: Dhan's tradingsymbols are not Kite's ("RELIANCE-Jul2026-FUT" vs
/// "RELIANCE26JULFUT"), and securityIds are not instrument_tokens. Spread definitions live in
/// memory only and trigger rules are cleared daily, so the migration path is simply to rebuild the
/// universe (POST /api/universe/build) once after switching Broker:Platform and restarting.
/// </summary>
public sealed class DhanInstrumentMasterService : IBrokerInstrumentMaster
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly DhanOptions _options;
    private readonly ILogger<DhanInstrumentMasterService> _logger;
    private readonly string _futuresCsvPath;
    private readonly HashSet<string> _exchanges;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<BrokerInstrument> _instruments = new();

    /// <summary>Both of Dhan's symbol spellings for each contract point at the same instrument, so
    /// a lookup by either resolves. Dhan's own positions/orders responses report the trading
    /// symbol, but which of the two spellings they use is not something to guess at - accepting
    /// both removes the question. Keyed by "SYMBOL|EXCHANGE".</summary>
    private Dictionary<string, BrokerInstrument> _bySymbol = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <param name="exchanges">Canonical exchange codes to retain - ["NFO"] normally, ["NFO","MCX"]
    /// when the commodity add-on is enabled. See McxOptions.ResolveExchanges.</param>
    public DhanInstrumentMasterService(
        HttpClient httpClient,
        IOptions<DhanOptions> options,
        ILogger<DhanInstrumentMasterService> logger,
        string futuresCsvPath,
        IReadOnlyList<string>? exchanges = null)
    {
        _httpClient = httpClient;
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

            _logger.LogInformation("Downloading Dhan scrip master from {Url}...", _options.ScripMasterUrl);

            using var response = await _httpClient.GetAsync(
                _options.ScripMasterUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parsed = ParseCsv(stream);

            _instruments = parsed.Select(p => p.Instrument).ToList();
            _bySymbol = BuildSymbolIndex(parsed);
            _loadedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Instrument master ready: {Count} futures across [{Exchanges}] (options/equities and other exchanges discarded during parse)",
                _instruments.Count, string.Join(", ", _exchanges));

            if (_instruments.Count == 0)
            {
                _logger.LogWarning(
                    "Dhan scrip master parsed to zero futures contracts - check that Dhan:ScripMasterUrl points at the security id list CSV and that its column headers are recognised.");
            }
            else if (_instruments.All(i => i.TickSize == 0))
            {
                _logger.LogWarning(
                    "No tick sizes were found in the Dhan scrip master - Dhan:ScripMasterUrl is probably pointing at the COMPACT csv. " +
                    "Order pricing will fall back to a 0.05 default tick, which is wrong for any contract that doesn't use it. " +
                    "Use api-scrip-master-detailed.csv instead.");
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private bool IsFresh() => _instruments.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime;

    /// <summary>Indexes each contract under its primary trading symbol and, where Dhan publishes a
    /// different display spelling, under that too. Primary symbols are written first and aliases
    /// only fill gaps (TryAdd), so one contract's display name can never shadow another contract's
    /// real trading symbol.</summary>
    private static Dictionary<string, BrokerInstrument> BuildSymbolIndex(
        IReadOnlyList<(BrokerInstrument Instrument, string? Alias)> parsed)
    {
        var index = new Dictionary<string, BrokerInstrument>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instrument, _) in parsed)
            index[Key(instrument.TradingSymbol, instrument.Exchange)] = instrument;

        foreach (var (instrument, alias) in parsed)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                index.TryAdd(Key(alias, instrument.Exchange), instrument);
        }

        return index;
    }

    private static string Key(string symbol, string exchange) => $"{symbol}|{exchange}";

    public BrokerInstrument? Find(string tradingSymbol, string exchange)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol)) return null;

        if (_bySymbol.TryGetValue(Key(tradingSymbol, exchange), out var match))
            return match;

        // The caller may be holding a Dhan segment string ("NSE_FNO") rather than the canonical
        // exchange - normalize and retry before giving up.
        var canonical = DhanSymbology.ToCanonicalExchange(exchange);
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
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Expiry)
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
            ?? throw new InvalidOperationException($"Instrument not found in Dhan scrip master: {config.TradingSymbol} on {config.Exchange}");

        config.InstrumentToken = match.InstrumentToken;
        config.Expiry = match.Expiry;
        config.Underlying ??= match.Name;
        config.LotSize = match.LotSize;
        config.TickSize = match.TickSize;
    }

    /// <summary>Same CSV shape the Kite instrument master writes, so the symbol picker and anything
    /// reading the file on disk is unaffected by which broker produced it.</summary>
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

    // ---- CSV parsing ---------------------------------------------------------------------------

    /// <summary>Column names, with the aliases used across Dhan's compact and detailed files. The
    /// first name found in the header wins.</summary>
    private static class Columns
    {
        public static readonly string[] SecurityId = { "SEM_SMST_SECURITY_ID", "SECURITY_ID" };
        public static readonly string[] Exchange = { "SEM_EXM_EXCH_ID", "EXCH_ID" };
        public static readonly string[] InstrumentName = { "SEM_INSTRUMENT_NAME", "INSTRUMENT" };
        public static readonly string[] TradingSymbol = { "SEM_TRADING_SYMBOL", "TRADING_SYMBOL" };
        public static readonly string[] CustomSymbol = { "SEM_CUSTOM_SYMBOL", "DISPLAY_NAME" };
        public static readonly string[] UnderlyingName = { "SM_SYMBOL_NAME", "UNDERLYING_SYMBOL", "SYMBOL_NAME" };
        public static readonly string[] Expiry = { "SEM_EXPIRY_DATE", "SM_EXPIRY_DATE", "EXPIRY_DATE" };
        public static readonly string[] LotSize = { "SEM_LOT_UNITS", "LOT_SIZE" };
        public static readonly string[] TickSize = { "SEM_TICK_SIZE", "TICK_SIZE" };
    }

    /// <summary>Streams the CSV, keeping only futures on the configured exchanges. Returns each
    /// contract alongside Dhan's alternate display spelling for it (null when there isn't a
    /// distinct one), which BuildSymbolIndex folds into the lookup index.</summary>
    private List<(BrokerInstrument Instrument, string? Alias)> ParseCsv(Stream stream)
    {
        var result = new List<(BrokerInstrument Instrument, string? Alias)>();
        using var reader = new StreamReader(stream);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            _logger.LogError("Dhan scrip master was empty - no header row.");
            return result;
        }

        var header = BuildHeaderMap(SplitCsvLine(headerLine));

        var securityIdIndex = Resolve(header, Columns.SecurityId);
        var exchangeIndex = Resolve(header, Columns.Exchange);
        var instrumentNameIndex = Resolve(header, Columns.InstrumentName);
        var tradingSymbolIndex = Resolve(header, Columns.TradingSymbol);
        var customSymbolIndex = Resolve(header, Columns.CustomSymbol);
        var underlyingIndex = Resolve(header, Columns.UnderlyingName);
        var expiryIndex = Resolve(header, Columns.Expiry);
        var lotSizeIndex = Resolve(header, Columns.LotSize);
        var tickSizeIndex = Resolve(header, Columns.TickSize);

        // Without these four there is nothing usable to build - fail loudly rather than silently
        // returning an empty universe that looks like "the market has no futures today".
        if (securityIdIndex < 0 || exchangeIndex < 0 || instrumentNameIndex < 0 || tradingSymbolIndex < 0)
        {
            _logger.LogError(
                "Dhan scrip master is missing required columns (need a security id, exchange, instrument name and trading symbol). Header was: {Header}",
                headerLine);
            return result;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);

            var instrumentName = Get(fields, instrumentNameIndex);
            if (!DhanSymbology.IsFuture(instrumentName)) continue; // options/equities/indices - the bulk of the file

            var exchangeId = Get(fields, exchangeIndex);
            var canonicalExchange = ToCanonicalExchange(exchangeId);
            if (canonicalExchange is null || !_exchanges.Contains(canonicalExchange)) continue;

            if (!uint.TryParse(Get(fields, securityIdIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var securityId))
                continue;

            var tradingSymbol = Get(fields, tradingSymbolIndex);
            if (string.IsNullOrWhiteSpace(tradingSymbol)) continue;

            var customSymbol = Get(fields, customSymbolIndex);
            var underlying = Get(fields, underlyingIndex);
            if (string.IsNullOrWhiteSpace(underlying))
                underlying = DeriveUnderlying(tradingSymbol);

            var instrument = new BrokerInstrument(
                InstrumentToken: securityId,
                TradingSymbol: tradingSymbol,
                Name: underlying,
                Exchange: canonicalExchange,
                Segment: DhanSymbology.ToDhanSegment(canonicalExchange),
                // Normalized from FUTSTK/FUTIDX/FUTCOM to the app's canonical "FUT" - see
                // BrokerInstrument's contract.
                InstrumentType: "FUT",
                Expiry: ParseExpiry(Get(fields, expiryIndex)),
                LotSize: ParseInt(Get(fields, lotSizeIndex)),
                TickSize: ParseDecimal(Get(fields, tickSizeIndex)));

            // Carry the display spelling through as an alias so Find() resolves whichever of the
            // two a Dhan positions/orders response happens to report.
            var alias = !string.IsNullOrWhiteSpace(customSymbol) &&
                        !customSymbol.Equals(tradingSymbol, StringComparison.OrdinalIgnoreCase)
                ? customSymbol
                : null;

            result.Add((instrument, alias));
        }

        return result;
    }

    /// <summary>Dhan's exchange column is the exchange itself ("NSE"/"BSE"/"MCX"), not a segment.
    /// Combined with the futures-only filter already applied, an NSE row is by definition an NSE
    /// F&amp;O contract, so it maps to the canonical "NFO".</summary>
    private static string? ToCanonicalExchange(string? exchangeId) => exchangeId?.ToUpperInvariant() switch
    {
        "NSE" => "NFO",
        "BSE" => "BFO",
        "MCX" => "MCX",
        _ => null,
    };

    /// <summary>Fallback when the underlying column is absent: Dhan's trading symbols are
    /// "UNDERLYING-MonYYYY-FUT", so the leading segment before the first dash is the underlying.
    /// </summary>
    private static string DeriveUnderlying(string tradingSymbol)
    {
        var dash = tradingSymbol.IndexOf('-');
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

    private static string? Get(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index].Trim() : null;

    /// <summary>Dhan writes expiry as a date, sometimes with a time component appended. Take the
    /// date part either way; a blank means a non-dated contract and is left null.</summary>
    private static DateOnly? ParseExpiry(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var datePart = raw.Length > 10 ? raw[..10] : raw;
        if (DateOnly.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)
            ? DateOnly.FromDateTime(dateTime)
            : null;
    }

    private static int ParseInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>Dhan publishes tick size in the same units as the price. Some rows carry it as a
    /// float with trailing noise ("0.0500"), hence the invariant decimal parse.</summary>
    private static decimal ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    private static string[] SplitCsvLine(string line)
    {
        // Minimal quoted-field CSV split - the scrip master quotes symbol names containing commas.
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
