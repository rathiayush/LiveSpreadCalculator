using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.DhanIntegration.OptionsFlow;

/// <summary>
/// Builds the index option chain for the Options Flow add-on from Dhan's scrip master.
///
/// Reads the SAME csv DhanInstrumentMasterService already downloads and persists to disk, rather
/// than pulling Dhan's ~26 MB dump a second time: that file is written on every successful
/// refresh, so in a running deployment it is already there and current. A live download is only
/// attempted when the local copy is genuinely missing (first ever boot, or the futures master
/// hasn't completed its first load yet).
///
/// Only rows this add-on needs are retained - instrument type OPTIDX, for the configured indices
/// only - so enabling the feature adds one index chain per configured underlying rather than the
/// tens of thousands of rows the full options universe would bring. Nothing here touches the
/// futures index the trading engine resolves orders against; see IOptionsInstrumentSource for why
/// the two are kept apart.
/// </summary>
public sealed class DhanOptionsInstrumentSource : IOptionsInstrumentSource
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    /// <summary>Dhan's instrument type for INDEX options. Stock options (OPTSTK) are deliberately
    /// not loaded - this add-on charts index chains only.</summary>
    private const string IndexOptionInstrumentType = "OPTIDX";

    private readonly HttpClient _httpClient;
    private readonly DhanOptions _options;
    private readonly OptionsFlowOptions _flowOptions;
    private readonly string _scripMasterCachePath;
    private readonly ILogger<DhanOptionsInstrumentSource> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>Chain keyed by "UNDERLYING|yyyy-MM-dd", each list holding both rights across every
    /// strike for that expiry.</summary>
    private Dictionary<string, List<OptionContract>> _chains = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public DhanOptionsInstrumentSource(
        HttpClient httpClient,
        IOptions<DhanOptions> options,
        IOptions<OptionsFlowOptions> flowOptions,
        string scripMasterCachePath,
        ILogger<DhanOptionsInstrumentSource> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _flowOptions = flowOptions.Value;
        _scripMasterCachePath = scripMasterCachePath;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_chains.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime) return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_chains.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime) return;

            var parsed = await LoadFromDiskAsync(cancellationToken) ?? await DownloadAsync(cancellationToken);
            if (parsed is null)
            {
                _logger.LogError("Could not obtain a Dhan scrip master to build the option chain from - the Options Flow page will have no data.");
                return;
            }

            _chains = parsed;
            _loadedAt = DateTimeOffset.UtcNow;

            foreach (var (key, contracts) in _chains.OrderBy(c => c.Key, StringComparer.Ordinal))
                _logger.LogInformation("Options Flow: loaded {Count} contracts for {Key}", contracts.Count, key);

            if (_chains.Count == 0)
            {
                _logger.LogWarning(
                    "Options Flow: no {Type} contracts matched the configured indices [{Indices}] in Dhan's scrip master.",
                    IndexOptionInstrumentType, string.Join(", ", _flowOptions.ResolveIndices()));
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Dictionary<string, List<OptionContract>>?> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_scripMasterCachePath)) return null;

        try
        {
            await using var stream = File.OpenRead(_scripMasterCachePath);
            var chains = ParseCsv(stream);
            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_scripMasterCachePath);
            _logger.LogInformation(
                "Options Flow: built the option chain from the local scrip master cache ({Path}, {Age:F1}h old).",
                _scripMasterCachePath, age.TotalHours);
            return chains;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Options Flow: could not read the local scrip master cache - falling back to a live download.");
            return null;
        }
    }

    private async Task<Dictionary<string, List<OptionContract>>?> DownloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Options Flow: downloading Dhan's scrip master to build the option chain...");
            using var response = await _httpClient.GetAsync(
                _options.ScripMasterUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return ParseCsv(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Options Flow: scrip master download failed.");
            return null;
        }
    }

    public DateOnly? GetNearestExpiry(string underlying)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)); // IST calendar date

        return _chains.Keys
            .Select(ParseKey)
            .Where(k => k.Expiry is not null
                        && string.Equals(k.Underlying, underlying, StringComparison.OrdinalIgnoreCase)
                        && k.Expiry.Value >= today)
            .Select(k => k.Expiry!.Value)
            .DefaultIfEmpty()
            .Min() is var min && min == default ? null : min;
    }

    public IReadOnlyList<OptionContract> GetChain(string underlying, DateOnly expiry) =>
        _chains.TryGetValue(Key(underlying, expiry), out var contracts)
            ? contracts
            : Array.Empty<OptionContract>();

    private static string Key(string underlying, DateOnly expiry) =>
        $"{underlying.ToUpperInvariant()}|{expiry:yyyy-MM-dd}";

    private static (string Underlying, DateOnly? Expiry) ParseKey(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 2) return (key, null);
        return (parts[0], DateOnly.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null);
    }

    // ---- CSV parsing -----------------------------------------------------------------------------

    private static class Columns
    {
        public static readonly string[] SecurityId = { "SEM_SMST_SECURITY_ID", "SECURITY_ID" };
        public static readonly string[] Exchange = { "SEM_EXM_EXCH_ID", "EXCH_ID" };
        public static readonly string[] InstrumentName = { "SEM_INSTRUMENT_NAME", "INSTRUMENT" };
        public static readonly string[] TradingSymbol = { "SEM_TRADING_SYMBOL", "TRADING_SYMBOL" };
        public static readonly string[] UnderlyingName = { "SM_SYMBOL_NAME", "UNDERLYING_SYMBOL", "SYMBOL_NAME" };
        public static readonly string[] Expiry = { "SEM_EXPIRY_DATE", "SM_EXPIRY_DATE", "EXPIRY_DATE" };
        public static readonly string[] LotSize = { "SEM_LOT_UNITS", "LOT_SIZE" };

        /// <summary>Options-only columns the futures master has no use for, which is why this
        /// class doesn't reuse its column map.</summary>
        public static readonly string[] Strike = { "SEM_STRIKE_PRICE", "STRIKE_PRICE" };
        public static readonly string[] OptionType = { "SEM_OPTION_TYPE", "OPTION_TYPE" };
    }

    private Dictionary<string, List<OptionContract>> ParseCsv(Stream stream)
    {
        var chains = new Dictionary<string, List<OptionContract>>(StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(_flowOptions.ResolveIndices(), StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(stream);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return chains;

        var header = BuildHeaderMap(SplitCsvLine(headerLine));
        var iSecurityId = Resolve(header, Columns.SecurityId);
        var iExchange = Resolve(header, Columns.Exchange);
        var iInstrument = Resolve(header, Columns.InstrumentName);
        var iSymbol = Resolve(header, Columns.TradingSymbol);
        var iUnderlying = Resolve(header, Columns.UnderlyingName);
        var iExpiry = Resolve(header, Columns.Expiry);
        var iLot = Resolve(header, Columns.LotSize);
        var iStrike = Resolve(header, Columns.Strike);
        var iRight = Resolve(header, Columns.OptionType);

        if (iSecurityId < 0 || iInstrument < 0 || iStrike < 0 || iRight < 0 || iExpiry < 0)
        {
            _logger.LogError(
                "Options Flow: the scrip master is missing columns required for an option chain (security id, instrument type, strike, option type, expiry). Header: {Header}",
                headerLine);
            return chains;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);

            if (!string.Equals(Get(fields, iInstrument), IndexOptionInstrumentType, StringComparison.OrdinalIgnoreCase))
                continue;

            var underlying = Get(fields, iUnderlying);
            if (string.IsNullOrWhiteSpace(underlying) || !wanted.Contains(underlying)) continue;

            var rightText = Get(fields, iRight);
            var right = string.Equals(rightText, "CE", StringComparison.OrdinalIgnoreCase) ? OptionRight.Call
                      : string.Equals(rightText, "PE", StringComparison.OrdinalIgnoreCase) ? OptionRight.Put
                      : (OptionRight?)null;
            if (right is null) continue;

            if (!uint.TryParse(Get(fields, iSecurityId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var securityId))
                continue;
            if (!decimal.TryParse(Get(fields, iStrike), NumberStyles.Float, CultureInfo.InvariantCulture, out var strike) || strike <= 0)
                continue;

            var expiry = ParseExpiry(Get(fields, iExpiry));
            if (expiry is null) continue;

            // Index options are always NSE F&O on Dhan; the exchange column is kept as a guard so a
            // BSE (SENSEX/BANKEX) row can never be silently mixed into an NSE chain.
            var exchangeId = Get(fields, iExchange);
            if (exchangeId is not null && !string.Equals(exchangeId, "NSE", StringComparison.OrdinalIgnoreCase))
                continue;

            var contract = new OptionContract(
                InstrumentToken: securityId,
                TradingSymbol: Get(fields, iSymbol) ?? $"{underlying}{strike:0}{rightText}",
                Underlying: underlying.ToUpperInvariant(),
                Exchange: "NFO",
                Segment: "NSE_FNO",
                Strike: strike,
                Right: right.Value,
                Expiry: expiry,
                LotSize: int.TryParse(Get(fields, iLot), out var lot) ? lot : 0);

            var key = Key(underlying, expiry.Value);
            if (!chains.TryGetValue(key, out var list))
                chains[key] = list = new List<OptionContract>();
            list.Add(contract);
        }

        return chains;
    }

    private static DateOnly? ParseExpiry(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var datePart = raw.Length > 10 ? raw[..10] : raw;
        if (DateOnly.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;
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
