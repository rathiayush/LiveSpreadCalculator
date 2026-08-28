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
public sealed class DhanInstrumentMasterService : IBrokerInstrumentMaster, IOptionChainSource
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly DhanOptions _options;
    private readonly ILogger<DhanInstrumentMasterService> _logger;
    private readonly string _futuresCsvPath;
    private readonly string _scripMasterCachePath;
    private readonly HashSet<string> _exchanges;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<BrokerInstrument> _instruments = new();

    /// <summary>Cash equities + index levels, parsed from the same download as the futures above
    /// but held separately so the futures lookups keep their futures-only meaning. Read only
    /// through FindSpot - see the IBrokerInstrumentMaster doc comment.</summary>
    private List<BrokerInstrument> _spotInstruments = new();

    /// <summary>Index option (OPTIDX) contracts for the CDV feature's underlyings - see
    /// FindOption/GetOptionExpiries. NOT part of IBrokerInstrumentMaster (that interface stays
    /// futures-only by design, see its doc comment); these are extra methods on this concrete
    /// class only, consumed directly by CdvHostedService the same way DhanOrderGateway etc.
    /// already depend on this class concretely rather than through the interface.
    ///
    /// Narrowed to <see cref="_optionUnderlyings"/> while parsing - the option chain is the bulk
    /// of Dhan's ~200k-row scrip master (every strike x every weekly/monthly expiry x every
    /// underlying), so keeping only the couple of index underlyings this app actually streams CDV
    /// for avoids holding tens of thousands of irrelevant OPTSTK/OPTFUT rows in memory.</summary>
    private List<BrokerInstrument> _optionInstruments = new();

    /// <summary>Underlying names to keep OPTIDX rows for - everything else is discarded during
    /// parse, same treatment as the futures-only filter above. Defaults to the two index chains
    /// the CDV feature ships with (see CdvOptions.Chains); a deployment that wires up CdvOptions
    /// with other underlyings should pass those names in here via the constructor.</summary>
    private readonly HashSet<string> _optionUnderlyings;

    /// <summary>"UNDERLYING|yyyy-MM-dd|STRIKE|CE-or-PE" -> the resolved contract. Keyed once at
    /// parse time (BuildOptionIndex) rather than filtered with LINQ on every lookup - FindOption is
    /// called once per strike per underlying every time CdvHostedService (re)resolves its strike
    /// chain, which is cheap either way, but this keeps it O(1) regardless of how large the option
    /// list grows.</summary>
    private Dictionary<string, BrokerInstrument> _optionsByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every distinct expiry date available per underlying, ascending - see
    /// GetNearestOptionExpiry, which is what "only fetch near expiry" (CDV only ever subscribes
    /// the nearest one) is built on.</summary>
    private Dictionary<string, List<DateOnly>> _optionExpiriesByUnderlying = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Both of Dhan's symbol spellings for each contract point at the same instrument, so
    /// a lookup by either resolves. Dhan's own positions/orders responses report the trading
    /// symbol, but which of the two spellings they use is not something to guess at - accepting
    /// both removes the question. Keyed by "SYMBOL|EXCHANGE".</summary>
    private Dictionary<string, BrokerInstrument> _bySymbol = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <param name="exchanges">Canonical exchange codes to retain - ["NFO"] normally, ["NFO","MCX"]
    /// when the commodity add-on is enabled. See McxOptions.ResolveExchanges.</param>
    /// <param name="scripMasterCachePath">Where the raw scrip master (exactly as downloaded, not
    /// the filtered futures list futuresCsvPath holds) is persisted to disk - see
    /// EnsureLoadedAsync's doc comment for why this exists alongside the in-memory cache rather
    /// than instead of it.</param>
    public DhanInstrumentMasterService(
        HttpClient httpClient,
        IOptions<DhanOptions> options,
        ILogger<DhanInstrumentMasterService> logger,
        string futuresCsvPath,
        IReadOnlyList<string>? exchanges = null,
        string? scripMasterCachePath = null,
        IReadOnlyList<string>? optionUnderlyings = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _futuresCsvPath = futuresCsvPath;
        _scripMasterCachePath = scripMasterCachePath ?? Path.Combine(
            Path.GetDirectoryName(futuresCsvPath) ?? ".", "dhan_scrip_master_cache.csv");
        _exchanges = new HashSet<string>(
            exchanges is { Count: > 0 } ? exchanges : new[] { "NFO" },
            StringComparer.OrdinalIgnoreCase);
        _optionUnderlyings = new HashSet<string>(
            optionUnderlyings is { Count: > 0 } ? optionUnderlyings : new[] { "NIFTY", "BANKNIFTY" },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads (or refreshes) the futures universe. The in-memory index (_instruments/_bySymbol)
    /// stays the ONLY thing Find/GetAllFutures/etc. read - that has to stay true, since those are
    /// called on hot paths (order placement, every position row) that cannot afford a disk read
    /// or a network call per lookup. What changed is what FEEDS that index and where the
    /// downloaded data lives between refreshes:
    ///
    ///  1. THE RAW SCRIP MASTER IS PERSISTED TO DISK on every successful download (see
    ///     PersistToDiskAsync), not just held in process memory. A process restart - which this
    ///     app's deployments (Production/pradeep-1/pradeep-2) go through often - used to mean
    ///     every restart re-downloaded the full ~26 MB from Dhan before anything could resolve a
    ///     symbol. Now the first EnsureLoadedAsync call on a fresh instance warms the in-memory
    ///     index from the LOCAL file first (a local disk read, effectively instant compared to a
    ///     network round trip), so lookups work immediately even before the live download below
    ///     completes, and it STAYS there - reloaded from that same file on every future restart -
    ///     until the next successful download replaces it.
    ///  2. A LIVE DOWNLOAD STILL ALWAYS RUNS after the disk warm, in the same call - the disk file
    ///     is a fast starting point, never treated as good enough on its own. Only once that live
    ///     download fails outright (network down, Dhan unreachable) does this fall back to
    ///     whatever is already in memory (from the disk warm, or a still-a-bit-stale earlier
    ///     cycle) rather than leaving the index empty - the same "never blank out already-loaded
    ///     data over a transient failure" rule this codebase already applies elsewhere
    ///     (PositionsCache's ResyncAsync, DhanApiClient's error handling). If there is truly
    ///     nothing to fall back to (first boot ever, no disk cache, and the network call fails),
    ///     this still throws, exactly as it always has - there is no data to serve either way, so
    ///     pretending otherwise would be worse than an honest failure.
    ///  3. WHEN THIS ACTUALLY RUNS is driven by InstrumentMasterRefreshHostedService, once a day
    ///     at a fixed IST time (default 06:00, ahead of the market open) rather than a short
    ///     polling interval - this app mostly trades monthly F&amp;O contracts, which don't need
    ///     intraday-fresh listings, so a straightforward once-a-day download is enough. The 12h
    ///     CacheLifetime below is only a guard against a redundant reload if something calls this
    ///     moments after the daily job already did.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (IsFresh()) return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh()) return;

            if (_instruments.Count == 0)
                TryWarmFromDiskCache();

            try
            {
                await RefreshFromDhanAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_instruments.Count == 0) throw; // nothing to fall back to - an honest failure

                _logger.LogWarning(ex,
                    "Live Dhan scrip master check failed - continuing with the {Count} futures already loaded " +
                    "({Source}) until the next refresh attempt.",
                    _instruments.Count, _loadedAt == DateTimeOffset.MinValue ? "from the local disk cache" : "from an earlier successful load");
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Best-effort warm start from the local disk cache - parses whatever was saved by
    /// the last successful PersistToDiskAsync call (this process or an earlier one) using the
    /// exact same ParseCsv the live path uses, since it is the identical raw Dhan format. Any
    /// failure (file missing on a fresh deployment, corrupt/partial file) is swallowed: this is
    /// purely a head start before the live check that always follows, never the only source of
    /// truth, so there is nothing to escalate here.</summary>
    private void TryWarmFromDiskCache()
    {
        if (!File.Exists(_scripMasterCachePath)) return;

        try
        {
            using var fileStream = File.OpenRead(_scripMasterCachePath);
            var parsed = ParseCsv(fileStream);
            if (parsed.Futures.Count == 0) return; // nothing usable - let the live check populate it instead

            _instruments = parsed.Futures.Select(p => p.Instrument).ToList();
            _spotInstruments = parsed.Spot;
            _bySymbol = BuildSymbolIndex(parsed.Futures);
            BuildOptionIndex(parsed.Options);

            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_scripMasterCachePath);
            _logger.LogInformation(
                "Warmed instrument master from local disk cache ({Count} futures, {Path}, downloaded {AgeHours:F1}h ago) - verifying against Dhan now.",
                _instruments.Count, _scripMasterCachePath, age.TotalHours);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the local scrip master cache at {Path} - falling back to a live download.", _scripMasterCachePath);
        }
    }

    /// <summary>The live download-and-parse step, plus persisting a successful download to disk
    /// (PersistToDiskAsync) so the next process restart gets the fast-warm-start benefit too.
    /// Always a full download - no conditional GET, since this only runs once a day (see
    /// EnsureLoadedAsync's doc comment), which makes the bandwidth/CPU cost of always fetching the
    /// whole file not worth the extra moving part of tracking an ETag.</summary>
    private async Task RefreshFromDhanAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading Dhan scrip master from {Url}...", _options.ScripMasterUrl);

        using var response = await _httpClient.GetAsync(
            _options.ScripMasterUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Buffered (rather than parsed straight off the network stream) because the same bytes
        // now need to go to two places - the parser and the disk cache - and 26 MB is a trivial,
        // one-time-per-day amount of memory for what runs as a background operation (see
        // InstrumentMasterRefreshHostedService), not something on a live request's critical path.
        var rawCsv = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        using var memoryStream = new MemoryStream(rawCsv, writable: false);
        var parsed = ParseCsv(memoryStream);

        _instruments = parsed.Futures.Select(p => p.Instrument).ToList();
        _spotInstruments = parsed.Spot;
        _bySymbol = BuildSymbolIndex(parsed.Futures);
        BuildOptionIndex(parsed.Options);
        _loadedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Instrument master ready: {Count} futures across [{Exchanges}], plus {SpotCount} cash/index instruments for spot pricing " +
            "and {OptionCount} index option contracts for [{OptionUnderlyings}] (other options/exchanges discarded during parse)",
            _instruments.Count, string.Join(", ", _exchanges), _spotInstruments.Count,
            _optionInstruments.Count, string.Join(", ", _optionUnderlyings));

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

        // Only after a successful parse - a corrupt or half-written cache file would be worse
        // than no cache file at all (TryWarmFromDiskCache would silently produce zero instruments
        // and fall through, costing a wasted disk read, but a PARTIAL file that parses into a
        // wrong/incomplete universe would not announce itself the same way).
        await PersistToDiskAsync(rawCsv, cancellationToken);
    }

    /// <summary>Atomically replaces the local scrip master cache file - same temp-file-then-move
    /// pattern SaveFuturesToCsvAsync already uses, so a process killed mid-write never leaves a
    /// half-written file for the next TryWarmFromDiskCache to trip over. Failures are logged, not
    /// thrown: the in-memory index this call is trying to back up already updated successfully, so
    /// a disk write failure costs only the next restart's fast-warm-start benefit, not correctness
    /// right now.</summary>
    private async Task PersistToDiskAsync(byte[] rawCsv, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_scripMasterCachePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tempPath = _scripMasterCachePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, rawCsv, cancellationToken);
            File.Move(tempPath, _scripMasterCachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the scrip master cache to {Path} - future restarts will re-download from Dhan.", _scripMasterCachePath);
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

    /// <summary>Indexes the parsed option rows by underlying/expiry/strike/right for FindOption,
    /// and collects each underlying's distinct expiries ascending for GetOptionExpiries/
    /// GetNearestOptionExpiry. Rebuilt wholesale on every load, same as _bySymbol - options expire
    /// and new weekly contracts appear constantly, so there is no per-row diffing, just replace
    /// the whole index from the latest parse.</summary>
    private void BuildOptionIndex(IReadOnlyList<BrokerInstrument> options)
    {
        _optionInstruments = options.ToList();

        var byKey = new Dictionary<string, BrokerInstrument>(StringComparer.OrdinalIgnoreCase);
        var expiries = new Dictionary<string, SortedSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            if (option.Expiry is not { } expiry || option.Strike is not { } strike || string.IsNullOrEmpty(option.OptionType))
                continue;

            byKey[OptionKey(option.Name, expiry, strike, option.OptionType)] = option;

            if (!expiries.TryGetValue(option.Name, out var set))
                expiries[option.Name] = set = new SortedSet<DateOnly>();
            set.Add(expiry);
        }

        _optionsByKey = byKey;
        _optionExpiriesByUnderlying = expiries.ToDictionary(
            kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static string OptionKey(string underlying, DateOnly expiry, decimal strike, string optionType) =>
        $"{underlying}|{expiry:yyyy-MM-dd}|{strike:0.####}|{optionType.ToUpperInvariant()}";

    /// <summary>Every expiry currently listed for this underlying's index options, ascending -
    /// empty if the underlying isn't in the configured option allow-list or nothing has loaded
    /// yet. Call EnsureLoadedAsync first.</summary>
    public IReadOnlyList<DateOnly> GetOptionExpiries(string underlying) =>
        _optionExpiriesByUnderlying.TryGetValue(underlying, out var list) ? list : Array.Empty<DateOnly>();

    /// <summary>The nearest (soonest) expiry that hasn't already passed <paramref name="asOf"/>,
    /// or null if none is listed. This is the ONLY expiry CdvHostedService ever subscribes -
    /// deliberately not the whole chain, since the far-dated monthly contracts add strikes nobody
    /// is trading without adding any useful order-flow signal.</summary>
    public DateOnly? GetNearestOptionExpiry(string underlying, DateOnly asOf) =>
        GetOptionExpiries(underlying).Where(e => e >= asOf).Cast<DateOnly?>().FirstOrDefault();

    /// <summary>Resolves one CE/PE contract by underlying + expiry + strike. Returns null rather
    /// than throwing - a strike that doesn't exist for this expiry (e.g. the chain doesn't extend
    /// that far OTM yet) is routine, not an error; callers skip it the same way
    /// TickIngestionHostedService skips an unresolvable spread leg.</summary>
    public BrokerInstrument? FindOption(string underlying, DateOnly expiry, decimal strike, bool isCall) =>
        _optionsByKey.TryGetValue(OptionKey(underlying, expiry, strike, isCall ? "CE" : "PE"), out var match)
            ? match
            : null;

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

        /// <summary>Option contracts only - confirmed present in Dhan's compact scrip master as
        /// SEM_STRIKE_PRICE (a plain rupee decimal, unlike tick size this is NOT in paise) and
        /// SEM_OPTION_TYPE ("CE"/"PE"; non-option rows carry "XX").</summary>
        public static readonly string[] Strike = { "SEM_STRIKE_PRICE", "STRIKE_PRICE" };
        public static readonly string[] OptionType = { "SEM_OPTION_TYPE", "OPTION_TYPE" };
    }

    /// <summary>Streams the CSV, keeping only futures on the configured exchanges. Returns each
    /// contract alongside Dhan's alternate display spelling for it (null when there isn't a
    /// distinct one), which BuildSymbolIndex folds into the lookup index.</summary>
    private ParsedMaster ParseCsv(Stream stream)
    {
        var result = new List<(BrokerInstrument Instrument, string? Alias)>();
        var spot = new List<BrokerInstrument>();
        var options = new List<BrokerInstrument>();
        using var reader = new StreamReader(stream);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            _logger.LogError("Dhan scrip master was empty - no header row.");
            return new ParsedMaster(result, spot, options);
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
        var strikeIndex = Resolve(header, Columns.Strike);
        var optionTypeIndex = Resolve(header, Columns.OptionType);

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
            return new ParsedMaster(result, spot, options);
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);

            var instrumentName = Get(fields, instrumentNameIndex);
            if (!DhanSymbology.IsFuture(instrumentName))
            {
                // Index options (OPTIDX) for the CDV feature's configured underlyings are kept too
                // - OPTSTK/OPTFUT/OPTCUR and every other underlying are still discarded, which is
                // what keeps this from pulling in the ~150k-row bulk of the file (every strike x
                // every expiry x every stock). See _optionUnderlyings/BuildOptionIndex.
                if (string.Equals(instrumentName?.Trim(), "OPTIDX", StringComparison.OrdinalIgnoreCase) &&
                    TryReadOptionRow(fields, securityIdIndex, exchangeIndex, segmentIndex, tradingSymbolIndex,
                        underlyingIndex, expiryIndex, lotSizeIndex, tickSizeIndex, strikeIndex, optionTypeIndex,
                        out var optionInstrument))
                {
                    options.Add(optionInstrument);
                    continue;
                }

                // Options are still discarded here (the bulk of the file), but cash equities and
                // index levels are kept - this same download already contains them, so spot pricing
                // costs no extra request. They go into their own list, never into the futures one.
                if (TryReadSpotRow(fields, instrumentName, securityIdIndex, exchangeIndex, tradingSymbolIndex, underlyingIndex, out var spotInstrument))
                    spot.Add(spotInstrument);
                continue;
            }

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

        return new ParsedMaster(result, spot, options);
    }

    /// <summary>Parses one OPTIDX row into a BrokerInstrument carrying Strike/OptionType, narrowed
    /// to <see cref="_optionUnderlyings"/> - everything else (wrong underlying, unparsable strike/
    /// right/expiry) returns false so the caller falls through to the spot-row check instead.
    /// TickSize/LotSize reuse the exact same paise/decimal parsing as futures rows - Dhan's option
    /// rows use the identical encoding (confirmed: NSE option ticks are "5.0000" for Rs 0.05, same
    /// as futures' ParseTickSize doc comment already notes).</summary>
    private bool TryReadOptionRow(
        string[] fields,
        int securityIdIndex, int exchangeIndex, int segmentIndex, int tradingSymbolIndex,
        int underlyingIndex, int expiryIndex, int lotSizeIndex, int tickSizeIndex,
        int strikeIndex, int optionTypeIndex,
        out BrokerInstrument instrument)
    {
        instrument = null!;

        var tradingSymbol = Get(fields, tradingSymbolIndex);
        if (string.IsNullOrWhiteSpace(tradingSymbol)) return false;

        // Confirmed against a real download: Dhan leaves SM_SYMBOL_NAME BLANK on every NSE index
        // option (OPTIDX) row - unlike futures rows, which do populate it - so this column alone
        // silently dropped every NIFTY/BANKNIFTY option ever parsed. Same fallback the futures
        // parser already uses: Dhan's option trading symbols are "UNDERLYING-MonYYYY-STRIKE-CE/PE"
        // ("BANKNIFTY-Sep2026-72600-CE"), so the leading segment before the first dash is the
        // underlying either way.
        var underlying = Get(fields, underlyingIndex);
        if (string.IsNullOrWhiteSpace(underlying))
            underlying = DeriveUnderlying(tradingSymbol);
        if (string.IsNullOrWhiteSpace(underlying) || !_optionUnderlyings.Contains(underlying)) return false;

        var exchangeId = Get(fields, exchangeIndex);
        var canonicalExchange = ToCanonicalExchange(exchangeId, Get(fields, segmentIndex));
        if (canonicalExchange is null) return false;

        if (!uint.TryParse(Get(fields, securityIdIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var securityId))
            return false;

        var expiry = ParseExpiry(Get(fields, expiryIndex));
        if (expiry is null) return false;

        var optionType = Get(fields, optionTypeIndex)?.Trim().ToUpperInvariant();
        if (optionType is not ("CE" or "PE")) return false;

        var strike = ParseDecimal(Get(fields, strikeIndex));
        if (strike <= 0) return false;

        instrument = new BrokerInstrument(
            InstrumentToken: securityId,
            TradingSymbol: tradingSymbol,
            Name: underlying,
            Exchange: canonicalExchange,
            Segment: DhanSymbology.ToDhanSegment(canonicalExchange),
            InstrumentType: "OPT",
            Expiry: expiry,
            LotSize: ParseLotSize(Get(fields, lotSizeIndex)),
            TickSize: ParseTickSize(Get(fields, tickSizeIndex)),
            Strike: strike,
            OptionType: optionType);

        return true;
    }

    /// <summary>What one pass over the scrip master yields: the futures universe this app trades,
    /// and - from the very same rows, at no extra download cost - the cash/index instruments used
    /// only to price spot. Kept apart so every futures lookup stays futures-only.</summary>
    private sealed record ParsedMaster(
        List<(BrokerInstrument Instrument, string? Alias)> Futures,
        List<BrokerInstrument> Spot,
        List<BrokerInstrument> Options);

    /// <summary>
    /// Recognises the two non-futures row types worth keeping: NSE cash equities (EQUITY) and NSE
    /// index levels (INDEX). Everything else - options, currency, commodity cash - is left out.
    ///
    /// The Dhan segment is set explicitly rather than via ToCanonicalExchange, which is derivatives
    /// oriented and would map an NSE equity to NFO. Getting it right matters more here than
    /// elsewhere: a Dhan securityId is only unique WITHIN a segment, so a wrong segment would
    /// subscribe to a completely different contract that happens to share the id.
    /// </summary>
    private static bool TryReadSpotRow(
        string[] fields,
        string? instrumentName,
        int securityIdIndex,
        int exchangeIndex,
        int tradingSymbolIndex,
        int underlyingIndex,
        out BrokerInstrument instrument)
    {
        instrument = null!;

        var isIndex = string.Equals(instrumentName?.Trim(), "INDEX", StringComparison.OrdinalIgnoreCase);
        var isEquity = string.Equals(instrumentName?.Trim(), "EQUITY", StringComparison.OrdinalIgnoreCase);
        if (!isIndex && !isEquity) return false;

        // NSE only - this app's futures are all NSE-underlying, and a BSE row would collide on name.
        if (!string.Equals(Get(fields, exchangeIndex)?.Trim(), "NSE", StringComparison.OrdinalIgnoreCase)) return false;

        if (!uint.TryParse(Get(fields, securityIdIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var securityId))
            return false;

        var tradingSymbol = Get(fields, tradingSymbolIndex);
        if (string.IsNullOrWhiteSpace(tradingSymbol)) return false;

        var name = Get(fields, underlyingIndex);
        if (string.IsNullOrWhiteSpace(name)) name = tradingSymbol;

        instrument = new BrokerInstrument(
            InstrumentToken: securityId,
            TradingSymbol: tradingSymbol,
            Name: name,
            Exchange: isIndex ? "IDX" : "NSE",
            Segment: isIndex ? "IDX_I" : "NSE_EQ",
            InstrumentType: isIndex ? "INDEX" : "EQ",
            Expiry: null,
            LotSize: 0,
            TickSize: 0m);

        return true;
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
