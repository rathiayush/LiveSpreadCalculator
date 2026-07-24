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
///  2. THE COMPACT CSV IS THE ONE THAT PARSES. Dhan publishes a compact list
///     (api-scrip-master.csv) and a detailed one (api-scrip-master-detailed.csv). Only the compact
///     file has a per-contract trading symbol column (SEM_TRADING_SYMBOL); the detailed file's
///     nearest equivalent, SYMBOL_NAME, is not unique per contract - every MCX GOLD expiry shares
///     the value "GOLD" - so it cannot key the symbol index. Both files carry tick and lot size,
///     so the compact one loses nothing. DhanOptions.ScripMasterUrl therefore defaults to the
///     compact CSV, and pointing it at the detailed file is a hard, logged failure rather than a
///     partial load (see ParseCsv's required-column check).
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
                    "Dhan scrip master parsed to zero futures contracts - check that Dhan:ScripMasterUrl points at the compact security id list " +
                    "(https://images.dhan.co/api-data/api-scrip-master.csv) and that its column headers are recognised.");
            }
            else if (_instruments.All(i => i.TickSize == 0))
            {
                _logger.LogWarning(
                    "No tick sizes were found in the Dhan scrip master, so order pricing will fall back to a 0.05 default tick, " +
                    "which is wrong for any contract that doesn't use it. Expected a SEM_TICK_SIZE column in {Url}.",
                    _options.ScripMasterUrl);
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

        /// <summary>Needed alongside the exchange id to identify the segment - "NSE" alone is
        /// ambiguous between F&amp;O and currency derivatives. See ToCanonicalExchange.</summary>
        public static readonly string[] Segment = { "SEM_SEGMENT", "SEGMENT" };
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
        var segmentIndex = Resolve(header, Columns.Segment);
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
            // The overwhelmingly likely cause is being pointed at the DETAILED scrip master, which
            // has no per-contract trading symbol column at all, so say so outright: the symptom
            // (every symbol fails to resolve, no spread streams, no order can be placed) otherwise
            // looks nothing like "wrong CSV configured".
            _logger.LogError(
                "Dhan scrip master at {Url} is missing required columns (need security id, exchange, instrument name and trading symbol) - " +
                "no instruments were loaded, so no symbol will resolve and no order can be placed. " +
                "The detailed scrip master (api-scrip-master-detailed.csv) is NOT supported: it has no per-contract trading symbol column. " +
                "Set Dhan:ScripMasterUrl to the compact list, https://images.dhan.co/api-data/api-scrip-master.csv. Header was: {Header}",
                _options.ScripMasterUrl, headerLine);
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
            var canonicalExchange = ToCanonicalExchange(exchangeId, Get(fields, segmentIndex));
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
                LotSize: ParseLotSize(Get(fields, lotSizeIndex)),
                TickSize: ParseTickSize(Get(fields, tickSizeIndex)));

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

    /// <summary>
    /// Maps a scrip-master row's exchange to this app's canonical exchange code.
    ///
    /// The segment is what disambiguates it: "NSE" alone covers both the F&amp;O segment ("D") and
    /// the currency-derivatives segment ("C"), and both contain rows this app's futures filter
    /// accepts (FUTSTK/FUTIDX vs FUTCUR). Keying on the exchange id alone therefore filed all 129
    /// NSE currency futures under NFO, which put contracts nobody asked for into the NFO universe
    /// and - because the weekly currency contracts reuse a monthly-style trading symbol across
    /// different securityIds - introduced 16 duplicate symbols into the lookup index, where a
    /// Find() could return whichever row happened to be indexed last.
    ///
    /// A blank segment (a file variant that omits the column) falls back to the derivatives
    /// reading, which is the only segment this app trades.
    /// </summary>
    private static string? ToCanonicalExchange(string? exchangeId, string? segment)
    {
        var isCurrency = string.Equals(segment?.Trim(), "C", StringComparison.OrdinalIgnoreCase);

        return exchangeId?.ToUpperInvariant() switch
        {
            "NSE" => isCurrency ? "CDS" : "NFO",
            "BSE" => isCurrency ? "BCD" : "BFO",
            "MCX" => "MCX",
            _ => null,
        };
    }

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

    /// <summary>
    /// Lot size is a whole number of units, but Dhan writes it as a decimal string ("500.0"), which
    /// NumberStyles.Integer rejects outright - so parsing it as an integer returned 0 for every
    /// single contract in the file, not just the odd one. That is quiet but expensive: lot size is
    /// what converts a rule's "lots" into an order quantity and what the dashboard uses to offer
    /// "exit N lots", so a universe of zero-lot contracts breaks position sizing everywhere it is
    /// read. Parsed as a decimal and truncated instead.
    /// </summary>
    private static int ParseLotSize(string? raw)
    {
        var value = ParseDecimal(raw);
        return value > 0 && value <= int.MaxValue ? (int)value : 0;
    }

    private static decimal ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    /// <summary>
    /// Dhan publishes tick size in PAISE, not rupees - unlike every price on the wire, which is in
    /// rupees. The scrip master reports "5.0000" for a contract whose tick is actually Rs 0.05.
    /// Confirmed against contracts whose real tick size is independently known: NSE equity rows
    /// carry 1.0 (Rs 0.01), NSE option rows 5.0 (Rs 0.05), and on MCX, GOLD/SILVER/CRUDEOIL carry
    /// 100.0 (Rs 1.00), NATURALGAS 10.0 (Rs 0.10) and COPPER 5.0 (Rs 0.05) - all exactly 100x the
    /// true value, on both exchanges this app trades.
    ///
    /// This conversion is not cosmetic. MarketProtectionService aligns every protected LIMIT price
    /// to a multiple of this number, so leaving it in paise rounded order prices to the nearest
    /// Rs 10 instead of Rs 0.10 - on a 500-unit RELIANCE lot that is thousands of rupees of
    /// unintended slippage per leg, and a price the exchange may reject outright.
    /// </summary>
    private static decimal ParseTickSize(string? raw)
    {
        var paise = ParseDecimal(raw);
        return paise > 0 ? paise / 100m : 0m;
    }

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
